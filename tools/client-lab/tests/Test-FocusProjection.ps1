#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit\code\Lab.Common.ps1')
. (Join-Path $Workspace 'kit\code\Interaction.ps1')
$window=[pscustomobject]@{GuardCode='window_activation_unavailable';GuardObservationComplete=$true;GuardFlags=243;ClientWidth=1920;ClientHeight=1200;VisibleWindowCount=1;NativeErrorCode=$null;ActivationAttempted=$true;ActivationAccepted=$null}
if($null -ne (Get-LabWindowGuardObservation $window).focus){throw 'Legacy mock produced a diagnostic.'}
$trace=[pscustomobject]@{Outcome='provider_exception';Stage='set_focus';ForegroundAttempted=$true;ForegroundAccepted=$false;ProviderAttempted=$true;FailureCategory='invalid_operation';FailureHResult=-2146233079;NativeFailureCode=$null;ElapsedMilliseconds=200;Message='private-sentinel';Path='private-sentinel';StackTrace='private-sentinel';WindowTitle='private-sentinel';Enabled=$true;KeyboardFocusable=$true;Offscreen=$false;WindowPatternAvailable=$true;WindowVisualState=0;WindowReady=$true;Method='uia_set_focus'}
$window|Add-Member NoteProperty FocusAttempt $trace
$result=Get-LabWindowGuardObservation $window
$names=@($result.focus.Keys|Sort-Object)
if(($names -join ',') -cne 'elapsedMilliseconds,enabled,failureCategory,foregroundAccepted,foregroundAfter,foregroundAttempted,foregroundBefore,hresult,keyboardFocusable,method,nativeCode,offscreen,outcome,providerAttempted,stage,windowPatternAvailable,windowReady,windowVisualState'){throw 'Unexpected focus projection field.'}
if($result.focus.hresult -ne -2146233079 -or $result.focus.outcome -cne 'provider_exception' -or $result.focus.nativeCode -ne $null -or $result.focus.method -cne 'uia_set_focus'){throw 'Typed diagnostic fields were lost.'}
if($null -ne $result.focus.foregroundBefore -or $null -ne $result.focus.foregroundAfter){throw 'Legacy focus trace invented a foreground owner.'}
$trace|Add-Member NoteProperty ForegroundBefore ([pscustomobject]@{ProcessId=123;Executable='explorer.exe';WindowTitle='private-sentinel'})
$trace|Add-Member NoteProperty ForegroundAfter ([pscustomobject]@{ProcessId=124;Executable='C:\private-sentinel\app.exe';WindowHandle='private-sentinel'})
$result=Get-LabWindowGuardObservation $window
$json=@{interaction=@{windowGuard=$result};readiness=@{windowPreparation=$result}}|ConvertTo-Json -Depth 8
if($json.Contains('private-sentinel')){throw 'Private exception/window content leaked into the result.'}
$roundTrip=$json|ConvertFrom-Json
if($roundTrip.interaction.windowGuard.focus.stage -cne 'set_focus' -or $roundTrip.readiness.windowPreparation.focus.elapsedMilliseconds -ne 200){throw 'Result depth lost the focus projection.'}
$owner=$roundTrip.readiness.windowPreparation.focus
if($owner.foregroundBefore.processId -ne 123 -or $owner.foregroundBefore.executable -cne 'explorer.exe' -or
    $owner.foregroundAfter.processId -ne 124 -or $null -ne $owner.foregroundAfter.executable){throw 'Owner projection lost PID/name or emitted an image path.'}
$trace.ForegroundAfter.ProcessId=-1
if($null -ne (Get-LabFocusProjection $window).foregroundAfter){throw 'Invalid foreground PID was emitted.'}
$trace.Outcome='provider_timeout';$trace.Stage='element_lookup';$trace.FailureHResult=$null;$trace.FailureCategory='none'
$timeout=Get-LabFocusProjection $window
if($timeout.outcome -cne 'provider_timeout' -or $null -ne $timeout.hresult -or $timeout.stage -cne 'element_lookup'){throw 'Timeout projection was conflated with exception.'}
@{passed=$true;checks=9;runtime=$PSVersionTable.PSVersion.ToString();rawExceptionRetained=$false;nativeInputSent=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Workspace ('checks\focus-projection-ps'+$PSVersionTable.PSVersion.Major+'.json')) -Encoding utf8
'Focus projection passed: 9 checks; no native focus or input.'
