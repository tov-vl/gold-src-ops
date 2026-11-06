#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Workspace, [string]$ToolRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$module = Import-Module (Join-Path $ToolRoot 'Lab.CampaignRuntime.psm1') -Force -PassThru
$root = Join-Path $Workspace ('campaign-runtime-' + [guid]::NewGuid().ToString('N'))
$run = Join-Path $root 'ranks-en'
$null = New-Item -ItemType Directory -Path (Join-Path $run 'code')
$hashes = @{}
foreach ($name in @('Lab.Common.ps1','Broker.ps1','Worker.ps1','Expire.ps1','Uninstall.ps1','Worker-Quiet.js','Diagnostics.ps1','Interaction.ps1','Rules.Input.cs')) {
    [IO.File]::WriteAllText((Join-Path $run ('code/' + $name)), 'inert mock code')
    $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $run ('code/' + $name))).Hash
}
[IO.File]::WriteAllText((Join-Path $run 'activation.json'), (@{ targetName = 'LAB-EXAMPLE' } | ConvertTo-Json))
$status = @{
    protocol = 2; revision = 16; target = 'LAB-EXAMPLE'; leaseMode = 'manual'; expiresUtc = $null
    autonomousPanels = @('ranks','profile','topall'); pending = 0; terminalArchiveCandidates = @()
    worker = @{ state = 'ready'; client = @{ ready = $true; gameProcessCount = 0; utc = [DateTime]::UtcNow.ToString('o') } }
}
$update = @{ enabled = $true; blocked = $false; revision = 16; codeHashes = $hashes }
$context = @{ root = $root; run = $run; case = @{ jobId = [guid]::NewGuid().ToString('D'); panel = 'ranks'; language = 'en' }; campaign = @{ runtime = @{ pwsh = 'inert-pwsh' } } }
$calls = [Collections.Generic.List[object]]::new()
# Replace the process and OS ports inside this module only. No SSH/Docker call escapes.
& $module {
    param($Calls,$Status,$Update)
    $script:testCalls = $Calls; $script:testStatus = $Status; $script:testUpdate = $Update
    $script:testEndpoint = 'npipe:////./pipe/dockerDesktopLinuxEngine'
    $script:testDockerOccupied = $false
    function script:Get-NetUDPEndpoint { param($LocalPort,$ErrorAction) return }
    function script:Invoke-CampaignProcess([string]$File,[string[]]$Arguments,[int]$Seconds = 30) {
        $script:testCalls.Add(@{file=$File;arguments=$Arguments;seconds=$Seconds})
        if ($File -ceq 'docker') {
            if ($Arguments[0] -cne '--context' -or $Arguments[1] -cne 'desktop-linux') { throw 'Unexpected Docker target.' }
            if ($Arguments -contains 'inspect') { return $script:testEndpoint }
            if ($script:testDockerOccupied) { return 'owned-elsewhere' }
            return ''
        }
        if ($File -cne 'inert-pwsh' -or $Arguments[0] -cne '-NoLogo' -or $Arguments -notcontains '-NonInteractive') { throw 'Unexpected process.' }
        if (($Arguments -join ',') -cmatch 'Invoke-FramedUpdate.ps1,-Operation,status') { return $script:testUpdate | ConvertTo-Json -Depth 10 }
        if(($Arguments -join ',') -cmatch 'Invoke-Panel.ps1,-Operation,result'){return $script:testRetained|ConvertTo-Json -Depth 10}
        return $script:testStatus | ConvertTo-Json -Depth 10
    }
} $calls $status $update
$checks = 0
function Check([bool]$Value) { if (-not $Value) { throw 'Campaign adapter assertion failed.' }; $script:checks++ }
function Reject([scriptblock]$Action) { $failed=$false; try { $null = & $Action } catch { $failed=$true }; Check $failed }
Check ((Invoke-LabCampaignRuntime preflight $context).ready)
Check ($calls.Count -eq 4)
$null = Invoke-LabCampaignRuntime dispatch $context
Check (($calls[-1].arguments -join ',').EndsWith('-Operation,auto-panel,-JobId,' + $context.case.jobId + ',-Panel,ranks,-Language,en'))
$null = Invoke-LabCampaignRuntime cancel $context
Check (($calls[-1].arguments -join ',').EndsWith('-Operation,cancel,-JobId,' + $context.case.jobId))
Reject { Invoke-LabCampaignRuntime arbitrary $context }
& $module { $script:testEndpoint = 'tcp://remote.invalid:2376' }
Reject { Invoke-LabCampaignRuntime preflight $context }
& $module { $script:testEndpoint = 'npipe:////./pipe/dockerDesktopLinuxEngine'; $script:testDockerOccupied = $true }
Reject { Invoke-LabCampaignRuntime preflight $context }
& $module { $script:testDockerOccupied = $false; $script:testStatus.pending = 1 }
Reject { Invoke-LabCampaignRuntime preflight $context }
& $module { $script:testStatus.pending = 0; $script:testStatus.worker.client.utc = [DateTime]::UtcNow.AddMinutes(-5).ToString('o') }
Reject { Invoke-LabCampaignRuntime preflight $context }
& $module { $script:testStatus.worker.client.utc = [DateTime]::UtcNow.ToString('o'); $script:testUpdate.codeHashes['Worker.ps1'] = ('F' * 64) }
Reject { Invoke-LabCampaignRuntime preflight $context }
& $module { Assert-CampaignWeaponsReadiness @{} ranks }
& $module { Assert-CampaignWeaponsReadiness @{readinessEnabled=$true;nativeSetupEnabled=$true;setupCallback='gso_lab_ack'} guns }
$checks+=2
foreach($value in @($false,'true',$null)){
    Reject { & $module {param($Value) Assert-CampaignWeaponsReadiness @{readinessEnabled=$Value} guns} $value }
}
foreach($callback in @('gso_lab_setup_ack','gso_lab_ack_extra',$null)){
    Reject { & $module {param($Value) Assert-CampaignWeaponsReadiness @{readinessEnabled=$true;nativeSetupEnabled=$true;setupCallback=$Value} guns} $callback }
}
# Exactly one terminal failed renderer job may remain after accepted archival.
$retainedId=[guid]::NewGuid().ToString('D')
$status.pending=1
$status.worker.rendererRecovery=@{schema=1;status='archived';resultPreserved=$true;gameLaunched=$false;automaticRetryAllowed=$false;jobId=$retainedId;
    operationId=[guid]::NewGuid().ToString('D');planSha256=('b'*64);sha256=('a'*64);bytes=2877}
$retained=@{id=$retainedId;status='uncertain_cleanup';gamePid=1234;gameStartedUtc=[DateTime]::UtcNow.AddMinutes(-2).ToString('o');
    completedUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('o');cleanupVerified=$true;configCleanupVerified=$true;
    rendererEvidence=@{schema=1;jobId=$retainedId;status='unconfirmed';probeEnabled=$true;cleanupVerified=$false}}
& $module {param($Value) $script:testRetained=$Value} $retained
$before=$calls.Count
& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status
Check ($calls.Count -eq ($before+1) -and ($calls[-1].arguments -join ',').EndsWith('-Operation,result,-JobId,'+$retainedId))
foreach($field in @('resultPreserved','automaticRetryAllowed','gameLaunched')){
    $saved=$status.worker.rendererRecovery[$field];$status.worker.rendererRecovery[$field]='false'
    Reject {& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status}
    $status.worker.rendererRecovery[$field]=$saved
}
foreach($field in @('status','id','completedUtc','configCleanupVerified')){
    $saved=$retained[$field];$retained[$field]='invalid'
    Reject {& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status}
    $retained[$field]=$saved
}
$retained.rendererEvidence.cleanupVerified=$true
Reject {& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status}
$retained.rendererEvidence.cleanupVerified=$false
$retained.gamePid='1234'
Reject {& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status}
$retained.gamePid=1234;$status.worker.rendererRecovery.bytes='2877'
Reject {& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status}
$status.worker.rendererRecovery.bytes=2877
$status.pending=2
Reject {& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status}
$status.pending=8
Reject {& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status}
$status.pending=2;$status.terminalArchiveCandidates=@(@{id=$retainedId;sha256=('A'*64)})
Reject {& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status}
$status.pending=2;$status.terminalArchiveCandidates=@(@{id=$retainedId;sha256=('A'*64)},@{id=$retainedId;sha256=('A'*64)})
Reject {& $module {param($Context,$Status) Assert-CampaignQueue $Context $Status} $context $status}
$summary = @{ passed=$true; checks=$checks; targetNetworkUsed=$false; runtimeChanged=$false; nativeInputSent=$false }
[IO.File]::WriteAllText((Join-Path $Workspace 'campaign-runtime-summary.json'),($summary | ConvertTo-Json))
$summary | ConvertTo-Json
