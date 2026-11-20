#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
# Execute the actual Worker with isolated filesystem state and no process/network effects.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit\code\Lab.Common.ps1')
. (Join-Path $Workspace 'kit\code\Interaction.ps1')
$sandbox=Join-Path $Workspace ('checks\worker-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox|Out-Null
$commonStub=@'
function Assert-LabCode([string]$Root){}
function Assert-LabLease($Policy){}
# Hosted CI can use service session 0; this fixture models an interactive worker.
function Get-Process([int]$Id){if($Id -ne $PID){throw 'Unexpected process lookup.'};return [pscustomobject]@{SessionId=7}}
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
function Get-LabRendererPreflight($Policy){return @{schema=1;status='observed';gameClosed=$true;files=@()}}
function Get-LabRetainedRendererDiagnostics([string]$Root,$Policy){return @{schema=1;status='not_required';rendererAccepted=$false;files=@()}}
function New-LabRendererUnknown([string]$Id,[string]$Reason,[bool]$Enabled=$false){return @{schema=1;jobId=$Id;status='unconfirmed';reason=$Reason;probeEnabled=$Enabled;cleanupVerified=$false}}
function New-LabRendererProbe($Policy,[string]$Id){return @{enabled=($Policy.testCase -ceq 'desktop-lost');reason='test_fixture';paths=@()}}
function Add-LabRendererProgram([string]$Id,[string]$Program,[bool]$Enabled){return $Program}
function Add-LabRendererLaunchArguments([string]$Arguments,[bool]$Enabled){return $Arguments}
function Get-LabRendererEvidence($Policy,$Result,$Probe,[DateTime]$LaunchUtc){if($Policy.testCase -ceq 'renderer-fail'){throw 'Injected diagnostic failure.'};return New-LabRendererUnknown $Result.id 'test_disabled'}
function Remove-LabRendererLogs($Policy,$Result,$Probe,[DateTime]$LaunchUtc){return $true}
function Get-LabClientObservation($Policy){return $Policy}
function Get-LabClientReadiness($Observation){return @{ready=($Observation.testCase -cne 'busy');code='test-prerequisite'}}
function Get-LabDesktopLaunchState([string]$Root,$Policy){
    if($Policy.desktopState -ceq 'lost'){
        if(@(Get-ChildItem -LiteralPath (Join-Path $Policy.gameRoot 'cstrike') -Filter 'gso_lan_*.cfg').Count){return 'desktop_busy'}
        return 'ready'
    }
    return $Policy.desktopState
}
function Get-LabDesktopLaunchObservation([string]$Root,$Policy){
    $code=Get-LabDesktopLaunchState $Root $Policy
    $ownerCategory=switch($code){
        'ready' {'shell_desktop'}
        'desktop_busy' {'steam_web_helper'}
        'desktop_foreground_unverified' {'unverified'}
        default {'not_observed'}
    }
    return @{code=$code;ownerCategory=$ownerCategory}
}
function Start-LabDesktopHelper([string]$Root,[string]$Id){return Start-Process -FilePath (Join-Path $policy.steamRoot 'steam.exe') -ArgumentList '-applaunch 10 mock' -PassThru}
function Confirm-LabDesktopHelper($Process,[string]$Directory,[string]$Id){}
function Find-LabDispatchedGame($Policy,$Result,[DateTime]$LaunchUtc,[string]$Id){$Result.cleanupVerified=$true}
function Get-LabLogCheckpoint([string]$SteamRoot){return @{test=$true}}
function Get-LabLogEvidence([string]$SteamRoot,$Checkpoint,[DateTime]$LaunchUtc){return @{test=$true}}
'@
$interactionStub=@'
function Assert-LabInteractionBindings([string]$GameRoot){}
function Assert-LabGunsBindings([string]$GameRoot){}
function Wait-LabGunsReady([string]$Root,$Policy,$Job,$Result){
    $Result.gamePid=1234;$Result.gameStartedUtc=[DateTime]::UtcNow.ToString('o')
    if($Policy.testCase -cne 'success'){
        $Result.readiness=@{status='failed';failureCode='game_owner_token_open_failed';failurePhase='bind-game-process';failureElapsedSeconds=6.8;failureNativeError=5;automaticRetryAllowed=$false}
        throw 'Private injected readiness failure.'
    }
    return @{clock=$null;window=$null}
}
function Invoke-LabAutonomousGunsInteraction([string]$Root,$Policy,$Job,$Result,[hashtable]$Baseline,[DateTime]$LaunchUtc,[string]$ResultPath,$Readiness){Invoke-LabAutonomousRulesInteraction $Root $Policy $Job $Result $Baseline $LaunchUtc $ResultPath}
function Invoke-LabAutonomousRulesInteraction([string]$Root,$Policy,$Job,$Result,[hashtable]$Baseline,[DateTime]$LaunchUtc,[string]$ResultPath){
    $Result.gamePid=1234
    $Result.gameStartedUtc=[DateTime]::UtcNow.ToString('o')
    if($Policy.testCase -cne 'success'){throw 'Injected scenario failure.'}
    $Result.status='autonomous_captured_unreviewed'
}
'@
$cases=0
foreach($scenario in @('rules-current-session','rules-foreign-user','rules-success','rules-renderer-fail','rules-scenario-fail','rules-cleanup-fail','rules-busy','ranks-success','profile-success','topall-success','profile-scenario-fail','topall-cleanup-fail','ranks-busy','guns-success','guns-scenario-fail','guns-cleanup-fail','guns-busy','capture-guns-scenario-fail','guns-desktop-waits','guns-desktop-unavailable','guns-desktop-expires','guns-desktop-cancels','rules-desktop-lost','guns-idle-ready','guns-idle-desktop_busy','guns-idle-desktop_unavailable','guns-idle-desktop_session_inactive','guns-idle-desktop_foreground_unverified')){
    $panel,$name=$scenario -split '-',2
    $sessionCase=$name
    if($name -cin @('current-session','foreign-user')){$name='success'}
    if($panel -ceq 'capture'){$panel='guns-capture';$name='scenario-fail'}
    $root=Join-Path $sandbox $scenario
    foreach($directory in @('code','queue\steps','results','game\cstrike','steam')){New-Item -ItemType Directory -Path (Join-Path $root $directory) -Force|Out-Null}
    $common=[IO.File]::ReadAllText((Join-Path $Workspace 'kit\code\Lab.Common.ps1'))
    [IO.File]::WriteAllText((Join-Path $root 'code\Lab.Common.ps1'),$common+"`r`n"+$commonStub)
    $interaction=[IO.File]::ReadAllText((Join-Path $Workspace 'kit\code\Interaction.ps1'))
    [IO.File]::WriteAllText((Join-Path $root 'code\Interaction.ps1'),$interaction+"`r`n"+$interactionStub)
    [IO.File]::WriteAllText((Join-Path $root 'code\Diagnostics.ps1'),[IO.File]::ReadAllText((Join-Path $Workspace 'kit\code\Diagnostics.ps1'))+"`r`n"+$diagnosticsStub)
    foreach($file in @('game\hl.exe','game\cstrike\liblist.gam','steam\steam.exe')){[IO.File]::WriteAllText((Join-Path $root $file),'non-executable mock input')}
    $policy=@{gameUserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;gameSessionId=7;gameRoot=(Join-Path $root 'game');steamRoot=(Join-Path $root 'steam');testCase=$name;desktopState='ready'}
    switch($name){
        'desktop-waits' {$policy.testCase='success';$policy.desktopState='desktop_busy'}
        'desktop-unavailable' {$policy.desktopState='desktop_unavailable'}
        {$_ -cin @('desktop-expires','desktop-cancels')} {$policy.desktopState='desktop_busy'}
        'desktop-lost' {$policy.desktopState='lost'}
    }
    if($sessionCase -cin @('current-session','foreign-user')){$policy.gameSessionMode='current-user-interactive';$policy.gameSessionId++}
    if($sessionCase -ceq 'foreign-user'){$policy.gameUserSid='S-1-5-21-100-200-300-1003'}
    if($name.StartsWith('idle-')){
        $policy.desktopState=$name.Substring('idle-'.Length)
        Write-LabJson (Join-Path $root 'policy.json') $policy
        $started=[DateTime]::UtcNow
        & (Join-Path $Workspace 'kit\code\Worker.ps1') -Root $root
        $heartbeat=Read-LabJson (Join-Path $root 'results\worker.json')
        $sample=$heartbeat.desktopPreflight
        $sampleUtc=ConvertTo-LabUtc $sample.utc
        $expected=switch($policy.desktopState){
            'ready' {'shell_desktop'}
            'desktop_busy' {'steam_web_helper'}
            'desktop_foreground_unverified' {'unverified'}
            default {'not_observed'}
        }
        if($heartbeat.state -cne 'ready' -or $null -ne $heartbeat.launchAdmission -or
           $sample.schema -ne 1 -or $sample.code -cne $policy.desktopState -or $sample.ready -isnot [bool] -or
           $sample.ready -ne ($policy.desktopState -ceq 'ready') -or $sampleUtc -lt $started -or $sampleUtc -gt [DateTime]::UtcNow -or
           $sample.ownerCategory -cne $expected -or
           (($sample.PSObject.Properties.Name | Sort-Object) -join ',') -cne 'code,ownerCategory,ready,schema,utc' -or
           @(Get-ChildItem -LiteralPath (Join-Path $root 'queue') -Recurse -File).Count -or
           @(Get-ChildItem -LiteralPath (Join-Path $root 'results')).Count -ne 1 -or
           @(Get-ChildItem -LiteralPath (Join-Path $root 'game\cstrike') -Filter 'gso_lan_*.cfg').Count -or
           (Test-Path -LiteralPath (Join-Path $root 'launch-count.json'))){throw 'Idle desktop observation changed the launch or queue boundary.'}
        $cases++;continue
    }
    Write-LabJson (Join-Path $root 'policy.json') $policy
    $id=[guid]::NewGuid().ToString('D')
    $job=@{id=$id;action='verify-rules';language='en';createdUtc=[DateTime]::UtcNow.ToString('o')}
    $mode=@{id=$id;language='en';profile='rules-v299-1920x1200-v1'}
    if($panel -cne 'rules'){$job.action='verify-panel';$job.panel=$panel;$mode.panel=$panel;$mode.profile='motd-v299-'+$panel+'-1920x1200-v1'}
    if($panel -ceq 'guns'){$mode.profile='guns-v299-1920x1200-v4'}
    if($panel -ceq 'guns-capture'){$job.Remove('panel');$job.action='capture-guns';$mode.Remove('panel');$mode.profile='guns-baseline-v299-1920x1200-v4'}
    if($name -ceq 'desktop-expires'){$job.createdUtc=[DateTime]::UtcNow.AddMinutes(-4).ToString('o')}
    $paths=Get-LabPaths $root $id
    Write-LabJson $paths.request $job
    Write-LabJson (Join-Path $root ('queue\steps\'+$id+'.autonomous.json')) $mode
    if($name -ceq 'desktop-cancels'){[IO.File]::WriteAllText($paths.cancel,'cancelled')}
    $requestHash=(Get-FileHash -LiteralPath $paths.request).Hash
    & (Join-Path $Workspace 'kit\code\Worker.ps1') -Root $root
    if($sessionCase -ceq 'foreign-user'){
        $heartbeat=Read-LabJson (Join-Path $root 'results\worker.json')
        if($heartbeat.state -cne 'blocked' -or $heartbeat.desktopPreflight.code -cne 'worker_blocked' -or
           $heartbeat.desktopPreflight.ownerCategory -cne 'not_observed' -or $heartbeat.desktopPreflight.ready -or
           (Test-Path -LiteralPath (Join-Path $root ('results\'+$id))) -or (Test-Path -LiteralPath (Join-Path $root 'launch-count.json'))){throw 'Foreign user was permitted.'}
        $cases++;continue
    }
    if($name -cin @('desktop-waits','desktop-unavailable')){
        $heartbeat=Read-LabJson (Join-Path $root 'results\worker.json')
        $expected=if($name -ceq 'desktop-waits'){'steam_web_helper'}else{'not_observed'}
        if($heartbeat.state -cne 'ready' -or $heartbeat.launchAdmission.status -cne 'waiting' -or
           $heartbeat.launchAdmission.code -cne $policy.desktopState -or $heartbeat.launchAdmission.jobId -cne $id -or
           $heartbeat.desktopPreflight.code -cne $heartbeat.launchAdmission.code -or $heartbeat.desktopPreflight.ready -or
           $heartbeat.desktopPreflight.ownerCategory -cne $expected -or
           (ConvertTo-LabUtc $heartbeat.launchAdmission.expiresUtc) -ne (ConvertTo-LabUtc $job.createdUtc).AddMinutes(3) -or
           (Test-Path -LiteralPath $paths.result) -or (Test-Path -LiteralPath (Join-Path $root 'launch-count.json')) -or
           @(Get-ChildItem -LiteralPath (Join-Path $root 'game\cstrike') -Filter 'gso_lan_*.cfg').Count -or
           (Get-FileHash -LiteralPath $paths.request).Hash -cne $requestHash){throw 'Busy desktop consumed a request or attempted a launch.'}
        if($name -ceq 'desktop-unavailable'){$cases++;continue}
        $policy.desktopState='ready'
        Write-LabJson (Join-Path $root 'policy.json') $policy
        & (Join-Path $Workspace 'kit\code\Worker.ps1') -Root $root
        $heartbeat=Read-LabJson (Join-Path $root 'results\worker.json')
        if($null -ne $heartbeat.launchAdmission -or (Get-FileHash -LiteralPath $paths.request).Hash -cne $requestHash){throw 'A later ready cycle did not use the original request.'}
    }
    $result=Read-LabJson (Join-Path $root ('results\'+$id+'\result.json'))
    if($sessionCase -ceq 'current-session' -and $result.gameSessionId -ne 7){throw 'Worker did not pin its actual session.'}
    if($name -cin @('desktop-expires','desktop-cancels','desktop-lost')){
        $expectedFailure=switch($name){'desktop-expires' {'request_expired'} 'desktop-cancels' {'cancelled'} 'desktop-lost' {'desktop_busy'}}
        if($result.status -cne 'rejected_preflight' -or $result.failureCode -cne $expectedFailure -or -not $result.completedUtc -or
           $result.launchRequested -or $result.launchCommandDispatched -or (Test-Path -LiteralPath (Join-Path $root 'launch-count.json')) -or
           @(Get-ChildItem -LiteralPath (Join-Path $root 'game\cstrike') -Filter 'gso_lan_*.cfg').Count -or
           (Get-FileHash -LiteralPath $paths.request).Hash -cne $requestHash){throw 'Expired, cancelled or busy launch reached dispatch or altered its request.'}
        $candidate=@(Get-LabTerminalArchiveCandidates $root)
        if($candidate.Count -ne 1 -or $candidate[0].id -cne $id -or $candidate[0].sha256 -cne $requestHash){
            throw 'A terminal request without a launch was not eligible for the existing archive flow.'
        }
    }elseif($name -ceq 'busy'){
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
    if($result.failureStage -ceq 'spawn_readiness'){
        if($result.failureCode -cne 'spawn_readiness_failed' -or $result.readiness.failureCode -cne 'game_owner_token_open_failed' -or $result.readiness.failureNativeError -ne 5 -or
            $result.readiness.failurePhase -cne 'bind-game-process' -or $result.readiness.failureElapsedSeconds -ne 6.8 -or
            $result.readiness.automaticRetryAllowed -or ($result|ConvertTo-Json -Depth 12).Contains('Private injected')){throw 'Readiness failure report differs.'}
    }
    if($result.acceptancePassed -or $result.rendererVerified){throw 'Worker asserted visual acceptance.'}
    $cases++
}
[IO.File]::WriteAllText((Join-Path $Workspace ('checks\worker-cleanup-ps'+$PSVersionTable.PSVersion.Major+'.json')),(@{passed=$true;cases=$cases;runtime=$PSVersionTable.PSVersion.ToString();sandbox=$sandbox;nativeInputSent=$false;processesStarted=$false}|ConvertTo-Json))
Write-Output ('Worker cleanup and one-attempt cases passed: '+$cases+'. No processes or network started.')
