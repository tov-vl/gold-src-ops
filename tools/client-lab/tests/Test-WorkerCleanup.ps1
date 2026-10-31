#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
# Execute the actual Worker with isolated filesystem state and no process/network effects.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit\code\Lab.Common.ps1')
$sandbox=Join-Path $Workspace ('checks\worker-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox|Out-Null
$commonStub=@'
function Assert-LabCode([string]$Root){}
function Assert-LabLease($Policy){}
function Get-LabFixtureReady($Policy){return @{ready=$true}}
function Get-LabSnapshots([string]$GameRoot){return @()}
function Get-LabProcess($Policy,[int]$ProcessId){return @()}
function Stop-LabOwnedGame($Policy,$Result){
    [IO.File]::WriteAllText((Join-Path $Root 'stop-called.json'),'called')
    if($Policy.testCase -ceq 'cleanup-fail'){throw 'Simulated failed cleanup.'}
}
function Start-Process([string]$FilePath,[string]$ArgumentList,[switch]$PassThru){
    if(-not $FilePath.EndsWith('steam.exe') -or -not $ArgumentList.StartsWith('-applaunch 10 ')){throw 'Unexpected mock launch.'}
    $countPath=Join-Path $Root 'launch-count.json'
    $count=if(Test-Path -LiteralPath $countPath){[int][IO.File]::ReadAllText($countPath)}else{0}
    [IO.File]::WriteAllText($countPath,[string]($count+1))
    $process=[pscustomobject]@{HasExited=$true;ExitCode=0}
    $process|Add-Member ScriptMethod Dispose {}
    return $process
}
'@
$diagnosticsStub=@'
function Get-LabClientObservation($Policy){return $Policy}
function Get-LabClientReadiness($Observation){return @{ready=($Observation.testCase -cne 'busy');code='test-prerequisite'}}
function Start-LabDesktopHelper([string]$Root,[string]$Id){return Start-Process -FilePath (Join-Path $policy.steamRoot 'steam.exe') -ArgumentList '-applaunch 10 mock' -PassThru}
function Confirm-LabDesktopHelper($Process,[string]$Directory,[string]$Id){}
function Find-LabDispatchedGame($Policy,$Result,[DateTime]$LaunchUtc,[string]$Id){$Result.cleanupVerified=$true}
function Get-LabLogCheckpoint([string]$SteamRoot){return @{test=$true}}
function Get-LabLogEvidence([string]$SteamRoot,$Checkpoint,[DateTime]$LaunchUtc){return @{test=$true}}
'@
$interactionStub=@'
function Assert-LabInteractionBindings([string]$GameRoot){}
function Invoke-LabAutonomousRulesInteraction([string]$Root,$Policy,$Job,$Result,[hashtable]$Baseline,[DateTime]$LaunchUtc,[string]$ResultPath){
    $Result.gamePid=1234
    $Result.gameStartedUtc=[DateTime]::UtcNow.ToString('o')
    if($Policy.testCase -cne 'success'){throw 'Injected scenario failure.'}
    $Result.status='autonomous_captured_unreviewed'
}
'@
$cases=0
foreach($scenario in @('rules-success','rules-scenario-fail','rules-cleanup-fail','rules-busy','ranks-success','profile-success','topall-success','profile-scenario-fail','topall-cleanup-fail','ranks-busy')){
    $panel,$name=$scenario -split '-',2
    $root=Join-Path $sandbox $scenario
    foreach($directory in @('code','queue\steps','results','game\cstrike','steam')){New-Item -ItemType Directory -Path (Join-Path $root $directory) -Force|Out-Null}
    $common=[IO.File]::ReadAllText((Join-Path $Workspace 'kit\code\Lab.Common.ps1'))
    [IO.File]::WriteAllText((Join-Path $root 'code\Lab.Common.ps1'),$common+"`r`n"+$commonStub)
    $interaction=[IO.File]::ReadAllText((Join-Path $Workspace 'kit\code\Interaction.ps1'))
    [IO.File]::WriteAllText((Join-Path $root 'code\Interaction.ps1'),$interaction+"`r`n"+$interactionStub)
    [IO.File]::WriteAllText((Join-Path $root 'code\Diagnostics.ps1'),$diagnosticsStub)
    foreach($file in @('game\hl.exe','game\cstrike\liblist.gam','steam\steam.exe')){[IO.File]::WriteAllText((Join-Path $root $file),'non-executable mock input')}
    $policy=@{gameUserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;gameSessionId=(Get-Process -Id $PID).SessionId;gameRoot=(Join-Path $root 'game');steamRoot=(Join-Path $root 'steam');testCase=$name}
    Write-LabJson (Join-Path $root 'policy.json') $policy
    $id=[guid]::NewGuid().ToString('D')
    $job=@{id=$id;action='verify-rules';language='en';createdUtc=[DateTime]::UtcNow.ToString('o')}
    $mode=@{id=$id;language='en';profile='rules-v299-1920x1200-v1'}
    if($panel -cne 'rules'){$job.action='verify-panel';$job.panel=$panel;$mode.panel=$panel;$mode.profile='motd-v299-'+$panel+'-1920x1200-v1'}
    Write-LabJson (Join-Path $root ('queue\'+$id+'.json')) $job
    Write-LabJson (Join-Path $root ('queue\steps\'+$id+'.autonomous.json')) $mode
    & (Join-Path $Workspace 'kit\code\Worker.ps1') -Root $root
    $result=Read-LabJson (Join-Path $root ('results\'+$id+'\result.json'))
    if($name -ceq 'busy'){
        if($result.launchRequested -or (Test-Path -LiteralPath (Join-Path $root 'launch-count.json'))){throw 'Busy client attempted a launch.'}
    }else{
        if(-not $result.configCleanupVerified -or @(Get-ChildItem -LiteralPath (Join-Path $root 'game\cstrike') -Filter 'gso_lan_*.cfg').Count -or -not (Test-Path -LiteralPath (Join-Path $root 'stop-called.json'))){throw 'Worker did not attempt bounded cleanup.'}
        if($name -ceq 'cleanup-fail'){
            if($result.cleanupVerified -or $result.status -cne 'uncertain_cleanup'){throw 'Cleanup failure was hidden.'}
        }elseif(-not $result.cleanupVerified){throw 'Successful mocked cleanup was not recorded.'}
        if([IO.File]::ReadAllText((Join-Path $root 'launch-count.json')) -cne '1'){throw 'Unexpected launch count.'}
        & (Join-Path $Workspace 'kit\code\Worker.ps1') -Root $root
        if([IO.File]::ReadAllText((Join-Path $root 'launch-count.json')) -cne '1'){throw 'Worker replayed a claimed job.'}
    }
    if($result.acceptancePassed -or $result.rendererVerified){throw 'Worker asserted visual acceptance.'}
    $cases++
}
[IO.File]::WriteAllText((Join-Path $Workspace ('checks\worker-cleanup-ps'+$PSVersionTable.PSVersion.Major+'.json')),(@{passed=$true;cases=$cases;runtime=$PSVersionTable.PSVersion.ToString();sandbox=$sandbox;nativeInputSent=$false;processesStarted=$false}|ConvertTo-Json))
Write-Output ('Worker cleanup and one-attempt cases passed: '+$cases+'. No processes or network started.')
