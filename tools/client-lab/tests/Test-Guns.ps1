#Requires -Version 5.1
param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference='Stop'
. (Join-Path $Workspace 'kit/code/Lab.Common.ps1')
. (Join-Path $Workspace 'kit/code/Interaction.ps1')
$root=Join-Path $Workspace ('checks/guns-'+$PSVersionTable.PSVersion.Major)
New-Item -ItemType Directory -Path $root -Force|Out-Null
$script:calls=[Collections.Generic.List[string]]::new()
$script:checks=0
function Check([bool]$Value){if(-not $Value){throw 'Weapons workflow assertion failed.'};$script:checks++}
function Reject([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed}
foreach($language in @('ru','en')){
    $id=[guid]::NewGuid().ToString('D')
    $request=ConvertFrom-LabRunnerCommand ('capture-guns '+$id+' '+$language)
    Check ($request.operation -ceq 'capture-guns' -and $request.language -ceq $language)
    $job=[pscustomobject]@{id=$id;action='capture-guns';language=$language;createdUtc=[DateTime]::UtcNow.ToString('o')}
    Assert-LabRunnerJob $job
    $program=New-LabGunsProgram $job
    Check (-not $program.Contains('exec ') -and -not $program.Contains('_stage'))
    Check ([Text.Encoding]::ASCII.GetByteCount($program) -le 32768)
    Check ((Get-LabRunnerImageLimit $job) -eq 4)
    $job.action='verify-panel';$job|Add-Member NoteProperty panel guns
    Assert-LabRunnerJob $job
    Check ((New-LabGunsProgram $job).Contains('gso_lab_setup_ack'))
    Check ((Get-LabAutonomousProfile $job) -ceq 'guns-v299-1920x1200-v2')
}
foreach($command in @('capture-guns x en','capture-guns '+[guid]::NewGuid().ToString()+' de','checkpoint '+[guid]::NewGuid().ToString()+' ../policy')){Reject {ConvertFrom-LabRunnerCommand $command}}
function Initialize-LabInteractionTypes([string]$Source){}
function Assert-LabGunsBindings([string]$GameRoot){}
function Get-LabAutoElapsed($Clock){return $script:now}
function Wait-LabAutoTick([int]$Milliseconds){$script:now+=$Milliseconds/1000.0}
function Assert-LabLease($Policy){if($script:case -ceq 'revoked'){throw 'Revoked.'}}
function Assert-LabInteractionOwner($Policy,$Result){if($script:case -ceq 'owner' -and $script:calls.Count){throw 'Changed owner.'}}
function Get-LabProcess($Policy){return [pscustomobject]@{Id=123;SessionId=7;StartTime=[DateTime]::UtcNow}}
function Add-GunsFrame($Result,[string]$Destination){
    $number=@($Result.images).Count+1;$path=Join-Path $Destination ('image-'+$number+'.bin')
    [IO.File]::WriteAllBytes($path,[Text.Encoding]::ASCII.GetBytes('bounded-frame-'+$number))
    $Result.images+=@{number=$number;sha256=(Get-FileHash $path).Hash;bytes=(Get-Item $path).Length}
}
function Receive-LabInteractionFrame($Policy,$Result,$Baseline,$Destination){Add-GunsFrame $Result $Destination;return $true}
function Save-LabOwnedWindowFrame($Window,$Result,$Destination){Add-GunsFrame $Result $Destination}
function Get-LabGunsAssessment($Bytes,$Key,$Language){
    if($script:case -ceq 'wrong-menu' -and $Key -ceq 'pistol-default'){throw 'Wrong menu.'}
    if($script:case -ceq 'wrong-first'){throw 'Wrong language.'}
    if($script:case -ceq 'stale'){$script:now+=121}
    return @{profile=$Key;sceneCandidate=($Key -ceq 'scene');panelMatches=($Key -cne 'scene');bodySha256=('A'*64)}
}
function New-Object([string]$TypeName,[object[]]$ArgumentList){
    if($TypeName -ceq 'Text.UTF8Encoding'){return [Text.UTF8Encoding]::new($false)}
    if($TypeName -cne 'GsoLab.Interaction.GameWindow'){throw 'Unexpected type.'}
    $window=[pscustomobject]@{ActivationAttempted=$false}
    $window|Add-Member ScriptMethod PrepareOwnWindow {}
    $window|Add-Member ScriptMethod CheckReady {if($script:case -ceq 'focus'){throw 'Focus denied.'}}
    $window|Add-Member ScriptMethod CaptureFrame {return [Text.Encoding]::ASCII.GetBytes('checkpoint')}
    $window|Add-Member ScriptMethod SelectGunsItem {param($Phase);$script:calls.Add($Phase);if($script:case -ceq 'uncertain'){throw 'Uncertain key delivery.'}}
    $window|Add-Member ScriptMethod MoveControls {$script:calls.Add('move')}
    $window|Add-Member ScriptMethod ReopenGuns {$script:calls.Add('reopen')}
    $window|Add-Member ScriptMethod Snapshot {}
    return $window
}
function Get-LabWindowGuardObservation($Window){return @{code='test'}}
foreach($name in @('success-ru','success-en','wrong-first','wrong-menu','focus','owner','revoked','stale','uncertain','claimed','cancelled')){
    $script:case=$name;$script:now=0.0;$script:calls.Clear()
    $directory=Join-Path $root $name;New-Item -ItemType Directory -Path $directory|Out-Null
    New-Item -ItemType Directory -Path (Join-Path $directory 'queue')|Out-Null
    $job=[pscustomobject]@{id=[guid]::NewGuid().ToString();action='verify-panel';panel='guns';language=$(if($name -ceq 'success-ru'){'ru'}else{'en'});createdUtc=[DateTime]::UtcNow.ToString('o')}
    if($name -ceq 'claimed'){[IO.File]::WriteAllText((Join-Path $directory 'auto-phase-exit-primary.json'),'claimed')}
    if($name -ceq 'cancelled'){[IO.File]::WriteAllText((Get-LabPaths $directory $job.id).cancel,'cancelled')}
    $result=[ordered]@{id=$job.id;images=@();gamePid=$null;gameStartedUtc=$null;status='launch_requested';readiness=@{status='ready';setupAcknowledged=$true;setupSnapshot=@{sha256=('A'*64)};setupObservedElapsed=0.0;windowPreparation=@{}};failureCode=$null;acceptancePassed=$false;rendererVerified=$false}
    $ready=@{window=(New-Object GsoLab.Interaction.GameWindow);clock=$null}
    $failed=$false
    try{Invoke-LabAutonomousGunsInteraction $directory ([pscustomobject]@{gameRoot=$directory;gameSessionId=7}) $job $result @{} ([DateTime]::UtcNow.AddSeconds(-1)) (Join-Path $directory 'result.json') $ready}catch{$failed=$true;if($name.StartsWith('success')){throw}}
    if($name.StartsWith('success')){
        Check (-not $failed -and $result.status -ceq 'autonomous_captured_unreviewed')
        Check (@($result.images).Count -eq 5 -and @($result.interaction.checkpoints).Count -eq 4 -and @($result.interaction.assessments).Count -eq 5)
        Check (($result.interaction.actions.phase -join ',') -ceq 'exit-primary,select-primary,select-pistol,repeat-primary,exit-pistol-respawn')
        Check (($script:calls -join ',') -ceq 'reopen,exit-primary,move,reopen,select-primary,select-pistol,move,reopen,repeat-primary,exit-pistol-respawn,move,move')
        Check (-not $result.acceptancePassed -and -not $result.rendererVerified)
    }else{
        Check $failed
        if($name -ceq 'uncertain'){Check (($script:calls -join ',') -ceq 'reopen,exit-primary')}
        if($name -cin @('cancelled','revoked','focus')){Check ($script:calls.Count -eq 0)}
        if($name -cin @('claimed','wrong-first','stale')){Check (($script:calls -join ',') -ceq 'reopen')}
        if($name -ceq 'wrong-menu'){Check (-not ($script:calls -contains 'select-pistol'))}
    }
}
Write-Output ('Weapons protocol/workflow checks passed: '+$script:checks+'. No target, native input or game.')
