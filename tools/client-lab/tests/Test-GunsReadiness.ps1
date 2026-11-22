#Requires -Version 5.1
param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit/code/Lab.Common.ps1')
. (Join-Path $Workspace 'kit/code/Interaction.ps1')
$root=Join-Path $Workspace ('checks/readiness-'+$PSVersionTable.PSVersion.Major+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$script:checks=0
$script:case='protocol'
function Check([bool]$Value){if(-not $Value){throw ('Readiness assertion failed: '+$script:case)};$script:checks++}
function Reject([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed}
$job=[pscustomobject]@{id=[guid]::NewGuid().ToString();action='capture-guns';language='ru';createdUtc=[DateTime]::UtcNow.ToString('o')}
$bootstrap=New-LabGunsProgram $job
Check (@($bootstrap -split "`r`n"|Where-Object {$_ -ceq 'snapshot' -or $_ -ceq 'guns' -or $_ -like 'jointeam *'}).Count -eq 0)
Check ($bootstrap.Contains('alias gso_lab_spawn "alias gso_lab_spawn; snapshot"'))
Check ($bootstrap.Contains('alias gso_lab_ack "alias gso_lab_ack; snapshot"'))
Check ($bootstrap.Contains('alias gso_lab_respawn "alias gso_lab_respawn; snapshot"'))
Check (-not $bootstrap.Contains('_set'))
Check (-not $bootstrap.Contains('exec ') -and -not $bootstrap.Contains('_stage'))
$arguments=Get-LabRunnerLaunchArguments $job ('gso_lan_'+([guid]$job.id).ToString('N')+'.cfg')
Check ($arguments.IndexOf('+exec ') -lt $arguments.IndexOf('+connect '))
New-Item -ItemType Directory -Path (Join-Path $root 'queue/steps') -Force|Out-Null
Check (-not (Get-LabAutonomousMode $root $job))
New-LabAutonomousMode $root $job
Check (Get-LabAutonomousMode $root $job)
$job.language='en'
Reject {Get-LabAutonomousMode $root $job}
$job.language='ru'
$policy=@{gameRoot=$root;gameSessionId=7;gameUserSid='fixture-sid'}
Reject {Assert-LabGunsBaseline @{images=@(@{sha256=('A'*64)},@{sha256=('A'*64)},@{sha256=('A'*64)},@{sha256=('A'*64)})} $root}
function Initialize-LabInteractionTypes([string]$Source){if($script:case -ceq 'bridge'){throw 'Private bridge details.'}}
function Assert-LabLease($Policy){if($script:case -ceq 'revoked'){throw 'Revoked.'}}
function Get-LabAutoElapsed($Clock){return $script:now}
function Wait-LabAutoTick([int]$Milliseconds){$script:now+=$(if($script:case -ceq 'setup-stale'){130}else{10})}
function Get-LabProcess($Policy,[int]$ProcessId){
    if($script:case -ceq 'no-process' -or ($script:case -ceq 'owner-changed' -and $ProcessId)){return}
    $p=[pscustomobject]@{Id=123;SessionId=7;StartTime=$script:gameStartedUtc}
    if($script:case -ceq 'ambiguous-process'){return @($p,$p)}
    return $p
}
function Get-LabGameOwnerCheck($Policy,$Result){
    if($Result.gamePid -ne 123 -or $Policy.gameUserSid -cne 'fixture-sid'){throw 'Owner check received the wrong process or SID.'}
    switch($script:case){
        'process-access' {return @{Code='game_process_open_failed';NativeError=5}}
        'reused-pid' {return @{Code='game_identity_changed';NativeError=$null}}
        'owner-access' {return @{Code='game_owner_token_open_failed';NativeError=5}}
        'owner-query' {return @{Code='game_owner_token_query_failed';NativeError=31}}
        'owner' {return @{Code='game_owner_sid_unverified';NativeError=$null}}
        default {return @{Code='verified';NativeError=$null}}
    }
}
function Receive-LabInteractionFrame($Policy,$Result,$Baseline,$Destination){
    if($script:case -ceq 'missing-spawn' -or ($script:case -ceq 'missing-setup-ack' -and $script:received -ge 1)){return $false}
    # Setup has not been triggered while waiting for this fixture acknowledgement.
    Check ($script:inputs -eq $(if($script:received -eq 0){0}else{5}))
    $path=Join-Path $Destination 'image-1.bin'
    [IO.File]::WriteAllText($path,('ack-marker-'+$script:received));$script:received++
    $Result.images+=@{number=1;sha256=(Get-FileHash $path).Hash}
    $Baseline[$path]=$true
    return $true
}
function New-Object([string]$TypeName,[object[]]$ArgumentList){
    if($TypeName -ceq 'Text.UTF8Encoding'){return [Text.UTF8Encoding]::new($false)}
    if($TypeName -cne 'GsoLab.Interaction.GameWindow'){throw 'Unexpected type.'}
    $window=[pscustomobject]@{}
    $window|Add-Member ScriptMethod PrepareOwnWindow {
        if($script:case -ceq 'prepare'){throw 'Window preparation refused.'}
        $script:prepared=$true
    }
    $window|Add-Member ScriptMethod CheckReady {if($script:case -ceq 'focus'){throw 'Focus unavailable.'};$script:guarded=$true}
    $window|Add-Member ScriptMethod GunsSetupAction {
        param($Phase)
        Check ($script:prepared -and $script:guarded)
        Check (Test-Path -LiteralPath (Join-Path $script:destination ('auto-phase-guns-setup-'+$Phase+'.json')))
        $script:inputs++
        if($script:case -ceq 'uncertain'){throw 'Uncertain setup request.'}
    }
    return $window
}
$expectedFailures=[ordered]@{
    'bridge'=@('bridge_initialization_failed','initialize')
    'owner-changed'=@('game_identity_changed','bind-game-process')
    'process-access'=@('game_process_open_failed','bind-game-process',5)
    'reused-pid'=@('game_identity_changed','bind-game-process')
    'owner-access'=@('game_owner_token_open_failed','bind-game-process',5)
    'owner-query'=@('game_owner_token_query_failed','bind-game-process',31)
    'owner'=@('game_owner_sid_unverified','bind-game-process')
    'ambiguous-process'=@('game_process_ambiguous','bind-game-process')
    'no-process'=@('game_process_not_observed','await-spawn-snapshot')
    'missing-spawn'=@('spawn_snapshot_missing','await-spawn-snapshot')
    'revoked'=@('lease_unverified','await-spawn-snapshot')
    'cancelled'=@('cancelled','await-spawn-snapshot')
    'prepare'=@('window_preparation_failed','prepare-window')
    'focus'=@('window_guard_failed','prepare-window')
    'claimed'=@('setup_claim_failed','open-settings')
    'uncertain'=@('setup_input_failed','open-settings')
    'setup-stale'=@('setup_snapshot_stale','open-languages')
    'missing-setup-ack'=@('setup_snapshot_missing','await-setup-snapshot')
}
foreach($case in (@('ready')+@($expectedFailures.Keys))){
    $script:case=$case;$script:now=0;$script:inputs=0;$script:received=0;$script:prepared=$false;$script:guarded=$false
    $script:gameStartedUtc=[DateTime]::UtcNow
    $script:destination=Join-Path $root $case
    New-Item -ItemType Directory -Path (Join-Path $destination 'queue') -Force|Out-Null
    if($case -ceq 'cancelled'){[IO.File]::WriteAllText((Get-LabPaths $destination $job.id).cancel,'cancelled')}
    if($case -ceq 'claimed'){[IO.File]::WriteAllText((Join-Path $destination 'auto-phase-guns-setup-open-settings.json'),'retained')}
    $result=@{images=@();gamePid=$null;gameStartedUtc=$null}
    $failed=$false
    try{$ready=Wait-LabGunsReady $destination $policy $job $result @{} ([DateTime]::UtcNow.AddSeconds(-1)) (Join-Path $destination 'result.json')}catch{$failed=$true;if($case -ceq 'ready'){throw}}
    if($case -ceq 'ready'){
        Check (-not $failed -and $result.readiness.status -ceq 'ready' -and $result.readiness.setupAcknowledged)
        Check ($script:inputs -eq 5 -and $result.images.Count -eq 0)
        Check ($null -ne $ready.window -and (Test-Path (Join-Path $destination 'readiness-spawn.bmp')))
        Check ($null -eq $result.readiness.failureCode -and $null -eq $result.readiness.failurePhase -and $null -eq $result.readiness.failureElapsedSeconds -and $null -eq $result.readiness.failureNativeError)
    }else{
        Check $failed
        Check ($script:inputs -eq $(if($case -cin @('uncertain','setup-stale')){1}elseif($case -ceq 'missing-setup-ack'){5}else{0}))
        Check ($result.readiness.status -ceq 'failed')
        Check ($result.readiness.failureCode -ceq $expectedFailures[$case][0])
        Check ($result.readiness.failurePhase -ceq $expectedFailures[$case][1])
        Check ($result.readiness.failureElapsedSeconds -eq $script:now)
        $expectedNativeError=if($expectedFailures[$case].Count -eq 3){$expectedFailures[$case][2]}
        Check ($result.readiness.failureNativeError -eq $expectedNativeError)
        Check (-not ($result.readiness | ConvertTo-Json -Depth 8).Contains('Private '))
        Check (-not $result.readiness.automaticRetryAllowed)
    }
}
$observe=[pscustomobject]@{id=[guid]::NewGuid().ToString('D');action='capture-guns';language='en';
    createdUtc=$job.createdUtc;rejoinPhase='observe'}
$script:case='observe-protocol'
$request=ConvertFrom-LabRunnerCommand ('guns-observe '+$observe.id)
Check ($request.operation -ceq 'guns-observe' -and $request.language -ceq 'en')
Reject {ConvertFrom-LabRunnerCommand ('guns-observe '+$observe.id+' en')}
Assert-LabRunnerJob $observe
New-LabAutonomousMode $root $observe
Check (Get-LabAutonomousMode $root $observe)
$observe.rejoinPhase='rejoin'
Reject {Get-LabAutonomousMode $root $observe}
$observe.rejoinPhase='observe'
Check ((Get-LabRunnerImageLimit $observe) -eq 1 -and (Get-LabRunnerCaptureLabels $observe) -ceq 'authorized-spawn')
$program=New-LabGunsProgram $observe
$lines=@($program.TrimEnd() -split '\r\n')
Check ($lines.Count -eq 2 -and $lines[0] -ceq 'alias gso_lab_join "alias gso_lab_join; jointeam 1; wait; joinclass 1"')
Check ($lines[1] -ceq 'alias gso_lab_steam_observed "alias gso_lab_steam_observed; snapshot"')
$server=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../fixture_steam_observer.sma'))
$callbacks=[regex]::Matches($server,'client_cmd\(player, "(gso_lab_[a-z_]+)"\)')
Check ($callbacks.Count -eq 1 -and $program.Contains('alias '+$callbacks[0].Groups[1].Value+' "'))

foreach($case in @('observe-ready','missing-spawn')){
    $script:case=$case;$script:now=0;$script:inputs=0;$script:received=0;$script:prepared=$false;$script:guarded=$false
    $script:gameStartedUtc=[DateTime]::UtcNow
    $script:destination=Join-Path $root ('observe-'+$case)
    New-Item -ItemType Directory -Path (Join-Path $destination 'queue') -Force|Out-Null
    $result=@{images=@();gamePid=$null;gameStartedUtc=$null}
    $failed=$false
    try{Wait-LabGunsReady $destination $policy $observe $result @{} ([DateTime]::UtcNow.AddSeconds(-1)) (Join-Path $destination 'result.json')|Out-Null}
    catch{$failed=$true;if($case -ceq 'observe-ready'){throw}}
    Check ($result.readiness.callback -ceq 'gso_lab_steam_observed' -and $result.readiness.mode -ceq 'steam-spawn-observe-v1')
    Check ($script:inputs -eq 0 -and -not $script:prepared -and -not $script:guarded -and -not $result.readiness.setupAcknowledged)
    Check (-not @(Get-ChildItem -LiteralPath $destination -Filter 'auto-phase-*.json').Count)
    if($case -ceq 'observe-ready'){
        Check (-not $failed -and $result.readiness.status -ceq 'snapshot-received' -and $result.images.Count -eq 1)
        Check ((Get-FileHash (Join-Path $destination 'image-1.bin')).Hash -ceq $result.readiness.spawnSnapshot.sha256)
    }else{
        Check ($failed -and $result.readiness.failureCode -ceq 'authorized_spawn_snapshot_missing' -and $result.images.Count -eq 0)
    }
}
$script:case='respawn-frame'
$script:destination=Join-Path $root 'ready'
$script:now=0
$script:ackAt=7
function Wait-LabAutoTick([int]$Milliseconds){$script:now+=$Milliseconds/1000}
function Receive-LabInteractionFrame($Policy,$Result,$Baseline,$Destination){
    if(@($Result.images).Count -ne 4){throw 'Frame accepted before callback.'}
    if($script:now -lt $script:ackAt){return $false}
    $Result.images+=@{number=5;sha256=('A'*64)}
    return $true
}
$window=[pscustomobject]@{}
$window|Add-Member ScriptMethod CheckReady {}
$frames=@(1..4|ForEach-Object {@{number=$_}})
$result=@{images=$frames;interaction=@{};failureCode=$null;gamePid=123;gameStartedUtc=$script:gameStartedUtc.ToString('o')}
Receive-LabGunsRespawnFrame $script:destination $policy $job $result $null $window @{} $script:destination
Check ($script:now -ge 7 -and $result.images.Count -eq 5 -and $result.interaction.respawnSnapshotSha256 -ceq ('A'*64))

$script:case='missing-respawn-frame'
$script:now=0
$script:ackAt=21
$result=@{images=$frames;interaction=@{};failureCode=$null;gamePid=123;gameStartedUtc=$script:gameStartedUtc.ToString('o')}
Reject {Receive-LabGunsRespawnFrame $script:destination $policy $job $result $null $window @{} $script:destination}
Check ($result.images.Count -eq 4 -and -not $result.interaction.ContainsKey('respawnSnapshotSha256') -and $result.failureCode -ceq 'guns_respawn_snapshot_missing')

Write-Output ('Weapons readiness checks passed: '+$checks+'. No target, game or native input.')
