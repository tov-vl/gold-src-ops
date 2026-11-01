#Requires -Version 5.1
param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit/code/Lab.Common.ps1')
. (Join-Path $Workspace 'kit/code/Interaction.ps1')
$root=Join-Path $Workspace ('checks/baseline-flow-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$script:checks=0
$script:effects=[Collections.Generic.List[string]]::new()
function Check([bool]$Value){if(-not $Value){throw 'Native baseline workflow assertion failed.'};$script:checks++}
function Assert-LabLease($Policy){}
function Get-LabAutoElapsed($Clock){return $script:seconds}
function Wait-LabAutoTick([int]$Milliseconds){$script:seconds+=1}
function Assert-LabInteractionOwner($Policy,$Result){if($script:case -ceq 'owner' -and $script:effects.Count -ge 2){throw 'Owner changed.'}}
function Receive-LabInteractionFrame($Policy,$Result,$Baseline,$Destination){
    if($script:case -ceq 'missing-frame'){return $false}
    $number=$Result.images.Count+1
    $path=Join-Path $Destination ('image-'+$number+'.bin')
    [IO.File]::WriteAllText($path,('native-frame-'+$number))
    $Result.images+=@{number=$number;sha256=(Get-FileHash $path).Hash}
    return $true
}
function Assert-LabGunsMenuStructure($Result,[string]$Destination){if($script:case -ceq 'structure'){throw 'Loading screen.'}}
function Assert-LabGunsBaseline($Result,[string]$Destination){Check ($Result.images.Count -eq 4);$Result.baselineStructureVerified=$true}
foreach($case in @('success-ru','success-en','focus','owner','missing-frame','structure','uncertain','capture-claimed','phase-claimed','cancelled')){
    $script:case=$case;$script:seconds=0;$script:effects.Clear()
    $directory=Join-Path $root $case
    New-Item -ItemType Directory -Path (Join-Path $directory 'queue') -Force|Out-Null
    $job=[pscustomobject]@{id=[guid]::NewGuid().ToString();action='capture-guns';language=$(if($case -ceq 'success-en'){'en'}else{'ru'});createdUtc=[DateTime]::UtcNow.ToString('o')}
    $window=[pscustomobject]@{}
    $window|Add-Member ScriptMethod CheckReady {if($script:case -ceq 'focus'){throw 'Focus lost.'}}
    $window|Add-Member ScriptMethod ReopenGuns {$script:effects.Add('open')}
    $window|Add-Member ScriptMethod Snapshot {$script:effects.Add('snapshot')}
    $window|Add-Member ScriptMethod SelectGunsItem {param($Phase);$script:effects.Add($Phase);if($script:case -ceq 'uncertain'){throw 'Uncertain key.'}}
    $result=@{images=@();readiness=@{setupSnapshot=@{sha256=('A'*64)};setupObservedElapsed=0.0};acceptancePassed=$false;rendererVerified=$false;status='running';failureCode=$null}
    if($case -ceq 'capture-claimed'){[IO.File]::WriteAllText((Join-Path $directory 'auto-phase-guns-capture-primary-default.json'),'retained')}
    if($case -ceq 'phase-claimed'){[IO.File]::WriteAllText((Join-Path $directory 'auto-phase-guns-baseline-select-primary.json'),'retained')}
    if($case -ceq 'cancelled'){[IO.File]::WriteAllText((Get-LabPaths $directory $job.id).cancel,'cancelled')}
    $failed=$false
    try{Invoke-LabGunsBaseline $directory @{} $job $result @{window=$window;clock=$null} @{} (Join-Path $directory 'result.json')}catch{$failed=$true;if($case.StartsWith('success')){throw}}
    if($case.StartsWith('success')){
        Check (-not $failed -and $result.status -ceq 'captured_unreviewed' -and $result.images.Count -eq 4)
        Check (($effects -join ',') -ceq 'open,snapshot,select-primary,snapshot,select-pistol,open,snapshot,repeat-primary,snapshot')
        Check (-not $result.acceptancePassed -and -not $result.rendererVerified)
    }else{
        Check $failed
        Check (-not ($effects -contains 'select-pistol'))
        if($case -cin @('cancelled','focus')){Check ($effects.Count -eq 0)}
        if($case -ceq 'capture-claimed'){Check (($effects -join ',') -ceq 'open')}
        if($case -ceq 'uncertain'){Check (($effects -join ',') -ceq 'open,snapshot,select-primary')}
    }
}
Write-Output ('Native baseline workflow checks passed: '+$checks+'. No target, window or input.')
