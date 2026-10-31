#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
# Test doubles replace every process/window/frame API. This never starts a game or sends input.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit\code\Lab.Common.ps1')
. (Join-Path $Workspace 'kit\code\Interaction.ps1')
$root=Join-Path $Workspace ('checks\workflow-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root 'queue\steps')|Out-Null
$script:calls=New-Object 'Collections.Generic.List[string]'
$script:failure=$null
$script:cancelled=$false
function Add-Type([string]$Path,[string[]]$ReferencedAssemblies){if(-not $Path.EndsWith('Rules.Input.cs') -or $ReferencedAssemblies.Count -lt 5){throw 'Unexpected type request.'}}
function New-Object([string]$TypeName,[object[]]$ArgumentList){
    if($TypeName -cne 'GsoLab.Interaction.GameWindow' -or $ArgumentList.Count -ne 4 -or $ArgumentList[0] -ne 1234 -or $ArgumentList[3] -ne 7){throw 'Unexpected window construction.'}
    $window=[pscustomobject]@{GuardCode='ready';GuardObservationComplete=$true;GuardFlags=251;ClientWidth=1920;ClientHeight=1200;VisibleWindowCount=1;NativeErrorCode=$null;ActivationAttempted=$false;ActivationAccepted=$null}
    $window|Add-Member ScriptMethod CheckReady {if($script:failure -ceq 'guard'){$this.GuardCode='client_size_mismatch';$this.ClientWidth=1536;$this.ClientHeight=960;throw 'Simulated window size guard.'};$script:calls.Add('ready')}
    $window|Add-Member ScriptMethod PrepareOwnWindow {if($script:failure -ceq 'activation'){$this.GuardCode='window_activation_denied';$this.ActivationAttempted=$true;$this.ActivationAccepted=$false;throw 'Simulated Windows focus denial.'};if($script:failure -ceq 'activated'){$this.ActivationAttempted=$true;$this.ActivationAccepted=$true};$this.CheckReady()}
    $window|Add-Member ScriptMethod ClickOk {if($script:failure -ceq 'click'){throw 'Simulated queue failure.'};$script:calls.Add('click')}
    $window|Add-Member ScriptMethod MoveControls {$script:calls.Add('move');if($script:failure -ceq 'move'){throw 'Simulated ownership change.'}}
    $window|Add-Member ScriptMethod ReopenRules {$script:calls.Add('alias')}
    $window|Add-Member ScriptMethod Snapshot {$script:calls.Add('snapshot')}
    return $window
}
function Write-LabJson([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 8))}
function Assert-LabLease($Policy){if($script:cancelled){throw 'Simulated lease revocation.'}}
function Get-LabProcess($Policy){return [pscustomobject]@{Id=1234;StartTime=[DateTime]::UtcNow;SessionId=7}}
function Assert-LabInteractionOwner($Policy,$Result){if($Result.gamePid -ne 1234){throw 'Unexpected owner.'}}
function Receive-LabInteractionFrame($Policy,$Result,[hashtable]$Baseline,[string]$Destination){
    $number=@($Result.images).Count+1
    $script:nativeFrames++
    if($number -eq 3){while([DateTime]::UtcNow -lt (ConvertTo-LabUtc $Result.interaction.reopenNotBeforeUtc)){Start-Sleep -Milliseconds 50}}
    $hash=([string]$number)*64
    if($script:failure -ceq 'activated'){
        $path=Join-Path $Destination ('image-'+$number+'.bin')
        [IO.File]::WriteAllText($path,('mock-native-frame-'+$script:nativeFrames))
        $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $Result.images+=@{number=$number;sha256=$hash}
    $Result.frameCapturedUtc=[DateTime]::UtcNow.ToString('o')
    if($number -lt 5 -and -not ($script:failure -ceq 'activated' -and $script:nativeFrames -eq 1)){
        $phase=Get-LabInteractionPhase $number
        $path=Join-Path $root ('queue\steps\'+$Result.id+'.'+$phase+'.json')
        Write-LabJson $path @{id=$Result.id;phase=$phase;frameSha256=$Result.images[-1].sha256;createdUtc=[DateTime]::UtcNow.ToString('o')}
    }
    return $true
}
function Save-LabOwnedWindowFrame($Window,$Result,[string]$Destination){
    $script:calls.Add('owned-capture')
    Receive-LabInteractionFrame $null $Result @{} $Destination|Out-Null
    $Result.images[-1]['source']='owned-client-dc'
}
function Run-Case([string]$Failure){
    $script:failure=$Failure
    $script:nativeFrames=0
    $script:calls.Clear()
    $job=[pscustomobject]@{id=[guid]::NewGuid().ToString('D');action='verify-rules';language='en';createdUtc=[DateTime]::UtcNow.ToString('o')}
    $result=[ordered]@{id=$job.id;gamePid=$null;gameStartedUtc=$null;images=@();labels=@();status='launch_requested';failureCode=$null}
    $resultPath=Join-Path $root ($job.id+'.json')
    $failureObserved=$false
    try{Invoke-LabRulesInteraction $root ([pscustomobject]@{gameRoot=$root;gameSessionId=7}) $job $result @{} ([DateTime]::UtcNow.AddSeconds(-1)) $resultPath}
    catch{$failureObserved=$true;if(($Failure -cin @('','activated')) -and -not $script:cancelled){throw}}
    return @{result=$result;failed=$failureObserved;calls=@($script:calls.ToArray())}
}
$success=Run-Case ''
if($success.failed -or $success.result.status -cne 'interaction_captured_unreviewed' -or @($success.result.images).Count -ne 5 -or
   @($success.result.interaction.actions).Count -ne 4 -or @($success.result.interaction.actions|Where-Object{-not $_.inputQueued}).Count){throw 'Complete mocked workflow failed.'}
$actions=@($success.calls|Where-Object{$_ -cne 'ready'})
if(($actions -join ',') -cne 'click,snapshot,move,snapshot,alias,owned-capture,click,move,snapshot'){throw 'Fixed action ordering differs.'}
if($success.result.interaction.controlReturn -cne 'not-proven' -or $success.result.interaction.okActivation -cne 'not-proven'){throw 'Mock transport incorrectly asserted client acceptance.'}
$activated=Run-Case 'activated'
if($activated.failed -or @($activated.result.images).Count -ne 5 -or $script:nativeFrames -ne 6 -or
   -not $activated.result.interaction.windowPreparation.snapshotQueued -or
   $activated.result.images[0].sha256 -ceq $activated.result.interaction.windowPreparation.beforeActivationFrame.sha256 -or
   (Get-FileHash -LiteralPath (Join-Path $root 'before-activation.bin') -Algorithm SHA256).Hash -cne $activated.result.interaction.windowPreparation.beforeActivationFrame.sha256){throw 'Activation did not preserve the old frame and require fresh native evidence.'}
$failed=Run-Case 'move'
if(-not $failed.failed -or @($failed.result.interaction.actions).Count -ne 2 -or $failed.result.interaction.actions[-1].inputQueued -or
   $failed.calls -contains 'alias' -or @($failed.calls|Where-Object{$_ -ceq 'snapshot'}).Count -ne 1){throw 'Failure did not stop forward input without retry.'}
$guard=Run-Case 'guard'
if(-not $guard.failed -or $guard.calls.Count -or $guard.result.interaction.windowGuard.code -cne 'client_size_mismatch' -or
   $guard.result.interaction.windowGuard.width -ne 1536 -or $guard.result.interaction.windowGuard.height -ne 960 -or
   -not $guard.result.interaction.windowGuard.foreground -or $guard.result.interaction.windowGuard.minimized -or -not $guard.result.interaction.windowGuard.sessionActive){throw 'Guard projection lost the rejection reason or allowed input.'}
$activation=Run-Case 'activation'
if(-not $activation.failed -or $activation.calls.Count -or $activation.result.interaction.windowPreparation.code -cne 'window_activation_denied' -or
   -not $activation.result.interaction.windowGuard.activationAttempted -or $activation.result.interaction.windowGuard.activationAccepted){throw 'Denied activation permitted input or lost its diagnostic.'}
$script:cancelled=$true
$cancel=Run-Case ''
if(-not $cancel.failed -or $cancel.calls.Count){throw 'Revoked lease allowed input.'}
[IO.File]::WriteAllText((Join-Path $Workspace ('checks\workflow-ps'+$PSVersionTable.PSVersion.Major+'.json')),(@{passed=$true;cases=6;runtime=$PSVersionTable.PSVersion.ToString();nativeInputSent=$false;processesStarted=$false;sandbox=$root}|ConvertTo-Json))
'Workflow cases passed: fixed sequence, fresh frame after activation, uncertain input, guard and denied-activation projection, revoked lease. No native input or game process.'
