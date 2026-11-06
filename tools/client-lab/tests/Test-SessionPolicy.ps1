#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit/code/Lab.Common.ps1')
. (Join-Path $Workspace 'kit/code/Diagnostics.ps1')
$checks=0
function Check([bool]$Value){if(-not $Value){throw 'Session boundary regression.'};$script:checks++}
function Refused([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed}
$sid='S-1-5-21-100-200-300-1002'
$policy=[pscustomobject]@{gameUserSid=$sid;gameSessionId=8;sessionId='c0a83f65-4188-4482-916f-2ce81d5441e0';port=2222}
$before=$policy|ConvertTo-Json -Compress
Check ((Resolve-LabSessionPolicy $policy $sid 8).gameSessionId -eq 8)
Refused {Resolve-LabSessionPolicy $policy $sid 9}
Refused {Resolve-LabSessionPolicy $policy 'S-1-5-21-100-200-300-1003' 8}
$policy|Add-Member gameSessionMode 'current-user-interactive'
foreach($session in @(1,8,9,33)){
    $effective=Resolve-LabSessionPolicy $policy $sid $session
    Check ($effective.gameSessionId -eq $session -and $policy.gameSessionId -eq 8 -and $effective.sessionId -ceq $policy.sessionId -and $effective.port -eq 2222)
}
Refused {Resolve-LabSessionPolicy $policy $sid 0}
Refused {Resolve-LabSessionPolicy $policy $sid -1}
Refused {Resolve-LabSessionPolicy $policy 'S-1-5-21-100-200-300-1003' 9}
foreach($invalid in @('CURRENT-USER-INTERACTIVE','any-session','',8,$null)){
    $policy.gameSessionMode=$invalid
    Refused {Resolve-LabSessionPolicy $policy $sid 9}
}
$policy.PSObject.Properties.Remove('gameSessionMode')
Check (($policy|ConvertTo-Json -Compress) -ceq $before)
# Session selection must not relax existing Steam and busy-game gates.
Check ((Get-LabClientReadiness ([pscustomobject]@{utc='now';workerUserMatches=$true;workerSessionMatches=$true;query='ok';truncated=$false;steam=@([pscustomobject]@{pathMatches=$true;ownerMatches=$true;sessionMatches=$true});game=@()})).ready)
foreach($case in @('foreign','wrong-session','multiple','busy','unavailable','wrong-path')){
    $entry=[pscustomobject]@{pathMatches=$true;ownerMatches=$true;sessionMatches=$true}
    $observation=[pscustomobject]@{schema=1;utc=[DateTime]::UtcNow.ToString('o');query='ok';truncated=$false;workerUserMatches=$true;workerSessionMatches=$true;steam=@($entry);game=@()}
    switch($case){
        foreign {$entry.ownerMatches=$false}
        wrong-session {$entry.sessionMatches=$false}
        multiple {$observation.steam=@($entry,$entry)}
        busy {$observation.game=@($entry)}
        unavailable {$observation.query='unavailable'}
        wrong-path {$entry.pathMatches=$false}
    }
    Check (-not (Get-LabClientReadiness $observation).ready)
}
@{passed=$true;checks=$checks;gameLaunched=$false;policyWritten=$false}|ConvertTo-Json
