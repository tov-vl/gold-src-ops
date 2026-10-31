#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
# All clock, process, window, frame and input effects are replaced by test ports.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit\code\Lab.Common.ps1')
. (Join-Path $Workspace 'kit\code\Interaction.ps1')
$sandbox=Join-Path $Workspace ('checks\autonomous-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $sandbox 'queue\steps')|Out-Null
$script:calls=New-Object 'Collections.Generic.List[string]'
$script:case=''
$script:now=0.0
$script:assessed=0
$script:nativeReads=0
$script:cancelled=$false
function Initialize-LabInteractionTypes([string]$Source){if(-not $Source.EndsWith('Rules.Input.cs')){throw 'Unexpected type source.'}}
function Get-LabAutoElapsed($Clock){return $script:now}
function Wait-LabAutoTick([int]$Milliseconds){$script:now+=$Milliseconds/1000.0}
function Assert-LabLease($Policy){if($script:case -ceq 'lease') {throw 'Revoked test lease.'}}
function Assert-LabInteractionOwner($Policy,$Result){
    if($script:case -ceq 'owner' -and $script:assessed -eq 2){throw 'Changed owner.'}
    if($script:case -ceq 'deadline' -and $script:assessed -eq 2){$script:now=441}
}
function Get-LabProcess($Policy){
    if($script:case -ceq 'missing-process'){return}
    $candidate=[pscustomobject]@{Id=1234;StartTime=[DateTime]::UtcNow;SessionId=7}
    if($script:case -ceq 'ambiguous'){return @($candidate,$candidate)}
    return $candidate
}
function New-Object([string]$TypeName,[object[]]$ArgumentList){
    if($TypeName -ceq 'Text.UTF8Encoding'){return [Text.UTF8Encoding]::new($false)}
    if($TypeName -cne 'GsoLab.Interaction.GameWindow' -or $ArgumentList[0] -ne 1234){throw 'Unexpected test window.'}
    $window=[pscustomobject]@{GuardCode='ready';GuardObservationComplete=$true;GuardFlags=251;ClientWidth=1920;ClientHeight=1200;VisibleWindowCount=1;NativeErrorCode=$null;ActivationAttempted=$false;ActivationAccepted=$null}
    $window|Add-Member ScriptMethod PrepareOwnWindow {
        if($script:case -ceq 'activation'){$this.GuardCode='window_activation_denied';throw 'Focus denied.'}
        if($script:case -ceq 'activated'){$this.ActivationAttempted=$true;$this.ActivationAccepted=$true}
        $script:calls.Add('prepare')
    }
    $window|Add-Member ScriptMethod CheckReady {
        if($script:case -ceq 'focus' -and $script:assessed -eq 2){$this.GuardCode='window_not_foreground';throw 'Focus lost.'}
    }
    $window|Add-Member ScriptMethod ClickOk {
        $script:calls.Add('click')
        if($script:case -ceq 'uncertain-input'){throw 'Uncertain queue delivery.'}
        if($script:case -ceq 'cancel'){[IO.File]::WriteAllText((Get-LabPaths $sandbox $script:jobId).cancel,'')}
    }
    $window|Add-Member ScriptMethod MoveControls {$script:calls.Add('move')}
    $window|Add-Member ScriptMethod ReopenRules {$script:calls.Add('alias');if($script:now-$script:clickedAt -lt 5){throw 'Cooldown violated.'}}
    return $window
}
function Add-TestFrame($Result,[string]$Destination){
    $number=@($Result.images).Count+1
    $path=Join-Path $Destination ('image-'+$number+'.bin')
    [IO.File]::WriteAllText($path,('fixture-frame-'+$number))
    $Result.images+=@{number=$number;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;bytes=(Get-Item -LiteralPath $path).Length}
    $Result.frameCapturedUtc=[DateTime]::UtcNow.ToString('o')
}
function Receive-LabInteractionFrame($Policy,$Result,[hashtable]$Baseline,[string]$Destination){
    $script:nativeReads++
    if($script:case -ceq 'snapshot-missing'){return $false}
    Add-TestFrame $Result $Destination
    return $true
}
function Save-LabOwnedWindowFrame($Window,$Result,[string]$Destination){
    if($script:case -ceq 'capture' -and @($Result.images).Count -eq 1){throw 'Frame capture failed.'}
    Add-TestFrame $Result $Destination
}
function Get-LabAutoFrameAssessment($Result,[string]$Destination,[string]$Language){
    $script:assessed++
    $number=@($Result.images).Count
    if($script:case -ceq 'stale' -and $number -eq 1){$script:now+=121}
    if($script:case -ceq 'slow-chat'){$script:calls.Add('no-controller-step-needed')}
    if($script:case -ceq 'digest'){throw 'Simulated digest mismatch.'}
    if($number -eq 1){$script:clickedAt=$script:now}
    $matches=$number -in @(1,4)
    if($script:case -ceq 'wrong-language' -and $number -eq 1){$matches=$false}
    if($script:case -ceq 'wrong-alias-panel' -and $number -eq 4){$matches=$false}
    $scene=$number -in @(2,3,5)
    if($script:case -ceq 'panel-still-open' -and $number -eq 2){$scene=$false;$matches=$true}
    return @{panelMatches=$matches;sceneCandidate=$scene;bodySha256=('A'*64);frameSha256=$Result.images[-1].sha256}
}
function Run-Case([string]$Name){
    $script:case=$Name;$script:now=0.0;$script:assessed=0;$script:nativeReads=0;$script:calls.Clear()
    $job=[pscustomobject]@{id=[guid]::NewGuid().ToString('D');action='verify-rules';language='en';createdUtc=[DateTime]::UtcNow.ToString('o')}
    $script:jobId=$job.id
    $directory=Join-Path $sandbox $job.id
    New-Item -ItemType Directory -Path $directory|Out-Null
    if($Name -ceq 'duplicate-phase'){[IO.File]::WriteAllText((Join-Path $directory 'auto-phase-ok-first.json'),'existing')}
    $result=[ordered]@{id=$job.id;gamePid=$null;gameStartedUtc=$null;images=@();labels=@();status='launch_requested';failureCode=$null;acceptancePassed=$false;rendererVerified=$false;panelVerified=$false;languageVerified=$false}
    $failed=$false
    try{Invoke-LabAutonomousRulesInteraction $sandbox ([pscustomobject]@{gameRoot=$sandbox;gameSessionId=7}) $job $result @{} ([DateTime]::UtcNow.AddSeconds(-1)) (Join-Path $directory 'result.json')}
    catch{$failed=$true;if($Name -cin @('success','activated','slow-chat')){throw}}
    return @{result=$result;failed=$failed;calls=@($script:calls.ToArray());seconds=$script:now;directory=$directory}
}
$count=0
foreach($name in @('success','activated','slow-chat')){
    $case=Run-Case $name
    if($case.failed -or $case.result.status -cne 'autonomous_captured_unreviewed' -or @($case.result.images).Count -ne 5 -or @($case.result.interaction.actions).Count -ne 4 -or @($case.result.interaction.assessments).Count -ne 5){throw 'Autonomous success evidence differs.'}
    if($case.result.acceptancePassed -or $case.result.rendererVerified -or $case.result.panelVerified -or $case.result.languageVerified -or $case.result.interaction.controlReturn -cne 'not-proven'){throw 'Scenario execution overclaimed acceptance.'}
    if((@($case.calls|Where-Object {$_ -cin @('click','move','alias')}) -join ',') -cne 'click,move,alias,click,move' -or $case.seconds -lt 5 -or $case.seconds -gt 20){throw 'Wrong fixed action sequence or budget.'}
    if(@(Get-ChildItem -LiteralPath $case.directory -Filter 'auto-phase-*.json').Count -ne 4){throw 'Permanent phase claims missing.'}
    $count++
}
foreach($name in @('activation','lease','ambiguous','missing-process','snapshot-missing','wrong-language','panel-still-open','wrong-alias-panel','focus','owner','stale','deadline','uncertain-input','cancel','capture','digest','duplicate-phase')){
    $case=Run-Case $name
    if(-not $case.failed -or $case.result.failureCode -cne 'autonomous_interaction_failed'){throw ('Failure was not stopped: '+$name)}
    if($name -cin @('activation','lease','ambiguous','missing-process','snapshot-missing','wrong-language','stale','digest','duplicate-phase') -and @($case.calls|Where-Object {$_ -cin @('click','move','alias')}).Count){throw ('Input preceded a rejected precondition: '+$name)}
    if($name -ceq 'uncertain-input' -and (@($case.calls|Where-Object {$_ -ceq 'click'}).Count -ne 1 -or @($case.result.interaction.actions).Count -ne 1 -or $case.result.interaction.actions[0].inputQueued)){throw 'Uncertain input was retried or asserted queued.'}
    if($name -cin @('focus','owner','deadline','cancel','panel-still-open','capture') -and @($case.result.interaction.actions).Count -ne 1){throw ('Failure continued input: '+$name)}
    $count++
}
# Strict opt-in marker, including the actual pinned job schema.
$job=[pscustomobject]@{id=[guid]::NewGuid().ToString('D');action='verify-rules';language='ru';createdUtc=[DateTime]::UtcNow.ToString('o')}
Assert-LabJob $job
if(Get-LabAutonomousMode $sandbox $job){throw 'Absent opt-in enabled automation.'}
New-LabAutonomousMode $sandbox $job
if(-not (Get-LabAutonomousMode $sandbox $job)){throw 'Valid opt-in was not read.'}
$rejected=$false;try{New-LabAutonomousMode $sandbox $job}catch{$rejected=$true};if(-not $rejected){throw 'Mode claim was overwritten.'}
$modePath=Join-Path $sandbox ('queue\steps\'+$job.id+'.autonomous.json')
Write-LabJson $modePath @{id=$job.id;language='en';profile='rules-v299-1920x1200-v1'}
$rejected=$false;try{Get-LabAutonomousMode $sandbox $job|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Wrong mode language accepted.'}
$count+=4
[IO.File]::WriteAllText((Join-Path $Workspace ('checks\autonomous-ps'+$PSVersionTable.PSVersion.Major+'.json')),(@{passed=$true;cases=$count;runtime=$PSVersionTable.PSVersion.ToString();nativeInputSent=$false;processesStarted=$false;sandbox=$sandbox}|ConvertTo-Json))
Write-Output ('Autonomous workflow cases passed: '+$count+'. No game, Steam, native focus or input.')
