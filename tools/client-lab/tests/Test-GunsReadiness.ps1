#Requires -Version 5.1
param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit/code/Lab.Common.ps1')
. (Join-Path $Workspace 'kit/code/Interaction.ps1')
$root=Join-Path $Workspace ('checks/readiness-'+$PSVersionTable.PSVersion.Major+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$script:checks=0
function Check([bool]$Value){if(-not $Value){throw 'Readiness assertion failed.'};$script:checks++}
function Reject([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed}
$job=[pscustomobject]@{id=[guid]::NewGuid().ToString();action='capture-guns';language='ru';createdUtc=[DateTime]::UtcNow.ToString('o')}
$bootstrap=New-LabGunsProgram $job
$stage=New-LabGunsStageProgram $job
Check (@($bootstrap -split "`r`n"|Where-Object {$_ -ceq 'snapshot' -or $_ -ceq 'guns' -or $_ -like 'jointeam *'}).Count -eq 0)
Check ($bootstrap.Contains('alias gso_lab_spawn "alias gso_lab_spawn; snapshot"'))
Check ($bootstrap.Contains(('exec '+(Get-LabGunsStageName $job))))
Check (-not $stage.Contains('jointeam') -and -not $stage.Contains('joinclass'))
$arguments=Get-LabRunnerLaunchArguments $job ('gso_lan_'+([guid]$job.id).ToString('N')+'.cfg')
Check ($arguments.IndexOf('+exec ') -lt $arguments.IndexOf('+connect '))
New-Item -ItemType Directory -Path (Join-Path $root 'queue/steps') -Force|Out-Null
Check (-not (Get-LabAutonomousMode $root $job))
New-LabAutonomousMode $root $job
Check (Get-LabAutonomousMode $root $job)
$job.language='en'
Reject {Get-LabAutonomousMode $root $job}
$job.language='ru'
New-Item -ItemType Directory -Path (Join-Path $root 'game/cstrike') -Force|Out-Null
$policy=@{gameRoot=(Join-Path $root 'game');gameSessionId=7}
$stagePath=Join-Path $policy.gameRoot ('cstrike/'+(Get-LabGunsStageName $job))
[IO.File]::WriteAllText($stagePath,$stage,[Text.Encoding]::ASCII)
Check (-not (Remove-LabGunsStage $policy $job ($stage+'changed')))
Check (Test-Path -LiteralPath $stagePath)
Check (Remove-LabGunsStage $policy $job $stage)
Check (-not (Test-Path -LiteralPath $stagePath))
Reject {Assert-LabGunsBaseline @{images=@(@{sha256=('A'*64)},@{sha256=('A'*64)},@{sha256=('A'*64)},@{sha256=('A'*64)})} $root}
function Initialize-LabInteractionTypes([string]$Source){}
function Assert-LabLease($Policy){if($script:case -ceq 'revoked'){throw 'Revoked.'}}
function Get-LabAutoElapsed($Clock){return $script:now}
function Wait-LabAutoTick([int]$Milliseconds){$script:now+=10}
function Get-LabProcess($Policy){
    if($script:case -ceq 'no-process'){return @()}
    $p=[pscustomobject]@{Id=123;SessionId=7;StartTime=[DateTime]::UtcNow}
    if($script:case -ceq 'ambiguous-process'){return @($p,$p)}
    return $p
}
function Assert-LabInteractionOwner($Policy,$Result){if($script:case -ceq 'owner'){throw 'Changed owner.'}}
function Receive-LabInteractionFrame($Policy,$Result,$Baseline,$Destination){
    if($script:case -ceq 'missing-spawn'){return $false}
    if($script:case -ceq 'ambiguous-frame'){throw 'Ambiguous frames.'}
    # Setup has not been triggered while waiting for this fixture acknowledgement.
    Check ($script:inputs -eq 0)
    $path=Join-Path $Destination 'image-1.bin'
    [IO.File]::WriteAllText($path,'spawn-marker')
    $Result.images+=@{number=1;sha256=(Get-FileHash $path).Hash}
    $Baseline[$path]=$true
    return $true
}
function New-Object([string]$TypeName,[object[]]$ArgumentList){
    if($TypeName -ceq 'Text.UTF8Encoding'){return [Text.UTF8Encoding]::new($false)}
    if($TypeName -cne 'GsoLab.Interaction.GameWindow'){throw 'Unexpected type.'}
    $window=[pscustomobject]@{}
    $window|Add-Member ScriptMethod PrepareOwnWindow {$script:prepared=$true}
    $window|Add-Member ScriptMethod CheckReady {if($script:case -ceq 'focus'){throw 'Focus unavailable.'};$script:guarded=$true}
    $window|Add-Member ScriptMethod ReopenGuns {
        Check ($script:prepared -and $script:guarded)
        Check (Test-Path -LiteralPath (Join-Path $script:destination 'auto-phase-guns-program-ready.json'))
        $script:inputs++
        if($script:case -ceq 'uncertain'){throw 'Uncertain setup request.'}
    }
    return $window
}
foreach($case in @('ready','missing-spawn','no-process','ambiguous-process','ambiguous-frame','owner','focus','revoked','cancelled','claimed','uncertain')){
    $script:case=$case;$script:now=0;$script:inputs=0;$script:prepared=$false;$script:guarded=$false
    $script:destination=Join-Path $root $case
    New-Item -ItemType Directory -Path (Join-Path $destination 'queue') -Force|Out-Null
    if($case -ceq 'cancelled'){[IO.File]::WriteAllText((Get-LabPaths $destination $job.id).cancel,'cancelled')}
    if($case -ceq 'claimed'){[IO.File]::WriteAllText((Join-Path $destination 'auto-phase-guns-program-ready.json'),'retained')}
    $result=@{images=@();gamePid=$null;gameStartedUtc=$null}
    $failed=$false
    try{$ready=Wait-LabGunsReady $destination $policy $job $result @{} ([DateTime]::UtcNow.AddSeconds(-1)) (Join-Path $destination 'result.json')}catch{$failed=$true;if($case -ceq 'ready'){throw}}
    if($case -ceq 'ready'){
        Check (-not $failed -and $result.readiness.status -ceq 'ready' -and $result.readiness.programRequested)
        Check ($script:inputs -eq 1 -and $result.images.Count -eq 0)
        Check ($null -ne $ready.window -and (Test-Path (Join-Path $destination 'readiness-spawn.bmp')))
    }else{
        Check $failed
        Check ($script:inputs -eq $(if($case -ceq 'uncertain'){1}else{0}))
        Check ($result.readiness.status -ceq 'failed')
    }
}
Write-Output ('Weapons readiness checks passed: '+$checks+'. No target, game or native input.')
