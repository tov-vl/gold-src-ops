#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit\code\Lab.Common.ps1')
. (Join-Path $Workspace 'kit\code\Interaction.ps1')
$window=[pscustomobject]@{GuardCode='window_activation_unavailable';GuardObservationComplete=$true;GuardFlags=243;ClientWidth=1920;ClientHeight=1200;VisibleWindowCount=1;NativeErrorCode=$null;ActivationAttempted=$true;ActivationAccepted=$null}
if($null -ne (Get-LabWindowGuardObservation $window).focus){throw 'Legacy mock produced a diagnostic.'}
$trace=[pscustomobject]@{Outcome='provider_exception';Stage='set_focus';ForegroundAttempted=$true;ForegroundAccepted=$false;ProviderAttempted=$true;FailureCategory='invalid_operation';FailureHResult=-2146233079;NativeFailureCode=$null;ElapsedMilliseconds=200;Message='private-sentinel';Path='private-sentinel';StackTrace='private-sentinel';WindowTitle='private-sentinel';Enabled=$true;KeyboardFocusable=$false;Offscreen=$false;WindowPatternAvailable=$true;WindowVisualState=0;WindowReady=$true;Method='uia_window_restore'}
$window|Add-Member NoteProperty FocusAttempt $trace
$result=Get-LabWindowGuardObservation $window
$names=@($result.focus.Keys|Sort-Object)
if(($names -join ',') -cne 'elapsedMilliseconds,enabled,failureCategory,foregroundAccepted,foregroundAttempted,hresult,keyboardFocusable,method,nativeCode,offscreen,outcome,providerAttempted,stage,windowPatternAvailable,windowReady,windowVisualState'){throw 'Unexpected focus projection field.'}
if($result.focus.hresult -ne -2146233079 -or $result.focus.outcome -cne 'provider_exception' -or $result.focus.nativeCode -ne $null){throw 'Typed diagnostic fields were lost.'}
$json=@{interaction=@{windowGuard=$result;windowPreparation=$result}}|ConvertTo-Json -Depth 8
if($json.Contains('private-sentinel')){throw 'Private exception/window content leaked into the result.'}
$roundTrip=$json|ConvertFrom-Json
if($roundTrip.interaction.windowGuard.focus.stage -cne 'set_focus' -or $roundTrip.interaction.windowPreparation.focus.elapsedMilliseconds -ne 200){throw 'Result depth lost the focus projection.'}
$trace.Outcome='provider_timeout';$trace.Stage='element_lookup';$trace.FailureHResult=$null;$trace.FailureCategory='none'
$timeout=Get-LabFocusProjection $window
if($timeout.outcome -cne 'provider_timeout' -or $null -ne $timeout.hresult -or $timeout.stage -cne 'element_lookup'){throw 'Timeout projection was conflated with exception.'}
@{passed=$true;checks=6;runtime=$PSVersionTable.PSVersion.ToString();rawExceptionRetained=$false;nativeInputSent=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Workspace ('checks\focus-projection-ps'+$PSVersionTable.PSVersion.Major+'.json')) -Encoding utf8
'Focus projection passed: 6 checks; no native focus or input.'
