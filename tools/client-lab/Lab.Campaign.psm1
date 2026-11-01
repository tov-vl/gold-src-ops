#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1')

function Read-CampaignJson([string]$Path) {
    $null = Assert-LabOutputPath $Path
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or $item.Length -gt 1MB) { throw 'Invalid campaign metadata.' }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
}

function Write-CampaignJson([string]$Path, $Value, [switch]$New) {
    $null = Assert-LabOutputPath $Path
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 30))
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        if ($New -or -not [IO.File]::Exists($Path)) { [IO.File]::Move($temporary, $Path) }
        else { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Test-CampaignTrue($Value) { return $Value -is [bool] -and $Value }
function Get-CampaignUtc { return [DateTime]::UtcNow.ToString('o') }
function ConvertTo-CampaignUtc($Value) {
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime }
    return [DateTimeOffset]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}
function Get-CampaignHash([string]$Path) {
    $ancestor = [IO.Path]::GetFullPath($Path)
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked input ancestor.' }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function New-CampaignClaim([string]$Path) {
    Write-CampaignJson $Path @{ utc = Get-CampaignUtc; oneAttempt = $true } -New
}

function Get-CampaignCase([string]$Root, [string]$Panel, [string]$Language) {
    if ($Panel -cnotin @('ranks','profile','topall','guns') -or $Language -cnotin @('ru','en')) { throw 'Fixed panel and language required.' }
    $null = Assert-LabOutputPath $Root
    $campaign = Read-CampaignJson (Join-Path $Root 'campaign.json')
    $guns=@($campaign.cases).Count -eq 2 -and @($campaign.cases|Where-Object {$_.panel -cne 'guns'}).Count -eq 0
    if ($campaign.schema -ne 1 -or (-not $guns -and @($campaign.cases).Count -ne 6)) { throw 'Invalid campaign.' }
    $case = @($campaign.cases | Where-Object { $_.panel -ceq $Panel -and $_.language -ceq $Language })
    if ($case.Count -ne 1 -or $case[0].key -cne "$Panel-$Language" -or
        $case[0].jobId -cnotmatch '\A[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\z' -or
        $case[0].jobId -ceq [guid]::Empty.ToString('D')) { throw 'Invalid case identity.' }
    $run = Join-Path $Root $case[0].key
    $statePath = Join-Path $run 'campaign-state.json'
    $state = Read-CampaignJson $statePath
    if ($state.jobId -cne $case[0].jobId -or $state.panel -cne $Panel -or $state.language -cne $Language) { throw 'State identity differs.' }
    return @{ root = $Root; campaign = $campaign; case = $case[0]; run = $run; statePath = $statePath; state = $state }
}

function Assert-CampaignPins($Context, [switch]$InputsOnly) {
    foreach ($pin in $Context.campaign.pins) {
        if ($pin.scope -ceq 'source') {
            if ($pin.name -cnotin @('Invoke-LabCampaign.ps1','Lab.Campaign.psm1','Lab.CampaignRuntime.psm1','lab_fixture.py','Lab.Tooling.psm1')) { throw 'Invalid source pin.' }
            if ($InputsOnly) { continue }
            $path = Join-Path $PSScriptRoot $pin.name
        } elseif ($pin.scope -ceq 'run') {
            if ($pin.name -cmatch '\A(?:(?:ranks|profile|topall|guns)-(?:ru|en)|engine)/[A-Za-z0-9_. /-]+\z' -and
                $pin.name -notmatch '(^|/)\.\.(/|$)') { $path = Join-Path $Context.root $pin.name }
            else { throw 'Invalid run pin.' }
        } else { throw 'Invalid pin scope.' }
        if ((Get-CampaignHash $path) -cne $pin.sha256) { throw 'Prepared input changed; no operation allowed.' }
    }
}

function Initialize-LabCampaign([string]$Root, [array]$Cases, [hashtable]$Runtime) {
    $null = Assert-LabOutputPath $Root
    $guns=$Cases.Count -eq 2 -and @($Cases|Where-Object {$_.panel -cne 'guns'}).Count -eq 0
    if ((-not $guns -and $Cases.Count -ne 6) -or @($Cases.key | Select-Object -Unique).Count -ne $Cases.Count) { throw 'Complete fixed campaign required.' }
    $pins = @()
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root 'engine') -Recurse -File)) {
        $pins += @{ scope = 'run'; name = [IO.Path]::GetRelativePath($Root,$file.FullName).Replace('\','/'); sha256 = Get-CampaignHash $file.FullName }
    }
    foreach ($case in $Cases) {
        if ($case.panel -cnotin @('ranks','profile','topall','guns') -or $case.language -cnotin @('ru','en') -or
            $case.key -cne ($case.panel + '-' + $case.language) -or $case.baselineSha256 -cnotmatch '\A[0-9A-F]{64}\z') { throw 'Invalid fixed case.' }
        $run = Join-Path $Root $case.key
        if($case.panel -ceq 'guns'){
            if(-not $guns -or $case.menuBaselines.Count -ne 4){throw 'Fixed weapons baseline matrix required.'}
            foreach($name in @('primary-default','pistol-default','primary-selected','pistol-selected')){
                if($case.menuBaselines[$name] -cnotmatch '\A[0-9A-F]{64}\z'){throw 'Weapons baseline missing.'}
            }
            $canonical=(@('primary-default','pistol-default','primary-selected','pistol-selected')|ForEach-Object {$_+':'+$case.menuBaselines[$_]}) -join "`n"
            if(@($case.menuBaselines.Values|Select-Object -Unique).Count -ne 4 -or
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))) -cne $case.baselineSha256){throw 'Weapons baseline identity differs.'}
        }elseif($guns){throw 'Mixed weapons campaign.'}
        $case.jobId = [guid]::NewGuid().ToString('D')
        foreach ($file in @(Get-ChildItem -LiteralPath $run -Recurse -File)) {
            $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\','/')
            $pins += @{ scope = 'run'; name = $relative; sha256 = Get-CampaignHash $file.FullName }
        }
        [IO.File]::WriteAllText((Join-Path $run 'campaign.lock'), '')
        Write-CampaignJson (Join-Path $run 'campaign-state.json') @{
            jobId = $case.jobId; panel = $case.panel; language = $case.language; phase = 'prepared'
            reason = $null; evidenceSha256 = $null; acceptancePassed = $false; utc = Get-CampaignUtc
        } -New
    }
    foreach ($name in @('Invoke-LabCampaign.ps1','Lab.Campaign.psm1','Lab.CampaignRuntime.psm1','lab_fixture.py','Lab.Tooling.psm1')) {
        $pins += @{ scope = 'source'; name = $name; sha256 = Get-CampaignHash (Join-Path $PSScriptRoot $name) }
    }
    [IO.File]::WriteAllText((Join-Path $Root 'availability.lock'), '')
    Write-CampaignJson (Join-Path $Root 'availability.json') @{ steamFree = $false; utc = Get-CampaignUtc } -New
    Write-CampaignJson (Join-Path $Root 'campaign.json') @{
        schema = 1; id = [guid]::NewGuid().ToString('D'); cases = $Cases; pins = $pins; runtime = $Runtime; utc = Get-CampaignUtc
    } -New
}

function Set-LabCampaignSteam([string]$Root, [bool]$Free) {
    $null = Read-CampaignJson (Join-Path $Root 'campaign.json')
    $null = Assert-LabOutputPath (Join-Path $Root 'availability.lock')
    $lock = [IO.File]::Open((Join-Path $Root 'availability.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Write-CampaignJson (Join-Path $Root 'availability.json') @{ steamFree = $Free; utc = Get-CampaignUtc } }
    finally { $lock.Dispose() }
}

function Test-CampaignHeld($Context) {
    $availability = Read-CampaignJson (Join-Path $Context.root 'availability.json')
    return -not (Test-CampaignTrue $availability.steamFree)
}

function Save-CampaignState($Context, [string]$Phase, [string]$Reason) {
    $Context.state.phase = $Phase
    $Context.state.reason = $Reason
    $Context.state.utc = Get-CampaignUtc
    Write-CampaignJson $Context.statePath $Context.state
}

function Send-CampaignCancel($Context, [scriptblock]$Port) {
    $path = Join-Path $Context.run 'cancel-send.json'
    if (Test-Path -LiteralPath $path) { return }
    # CreateNew arbitrates a concurrent operator cancel and the polling loop.
    try { New-CampaignClaim $path } catch { if (Test-Path -LiteralPath $path) { return }; throw }
    $null = & $Port 'cancel' $Context
}

function Stop-CampaignFixture($Context, [scriptblock]$Port) {
    if (-not (Test-Path -LiteralPath (Join-Path $Context.run 'fixture-claim.json'))) { return }
    $claim = Join-Path $Context.run 'stop-claim.json'
    if (-not (Test-Path -LiteralPath $claim)) {
        New-CampaignClaim $claim
        $null = & $Port 'stop' $Context
    }
    # A claimed stop is never repeated. Only absence is reconciled on resume.
    $absence = & $Port 'absence' $Context
    if (-not (Test-CampaignTrue $absence.verified)) { throw 'Owned fixture absence is unconfirmed.' }
    Write-CampaignJson (Join-Path $Context.run 'absence.json') $absence
}

function Assert-CampaignCleanup($Context, $Result) {
    if (-not (Test-CampaignTrue $Result.cleanupVerified) -or -not (Test-CampaignTrue $Result.configCleanupVerified)) { throw 'Client cleanup missing.' }
    $absence = Read-CampaignJson (Join-Path $Context.run 'absence.json')
    $cleanup = Read-CampaignJson (Join-Path $Context.run 'cleanup.json')
    $runner = Read-CampaignJson (Join-Path $Context.run 'runner-after.json')
    $finished = ConvertTo-CampaignUtc $Result.completedUtc
    $heartbeat = ConvertTo-CampaignUtc $runner.worker.client.utc
    if (-not (Test-CampaignTrue $absence.verified) -or -not (Test-CampaignTrue $cleanup.fixtureRemoved) -or
        -not (Test-CampaignTrue $runner.worker.client.ready) -or $runner.worker.state -cne 'ready' -or
        $runner.worker.client.gameProcessCount -ne 0 -or $heartbeat -lt $finished -or $finished -gt [DateTime]::UtcNow.AddSeconds(5) -or
        $heartbeat -gt [DateTime]::UtcNow.AddSeconds(5) -or (ConvertTo-CampaignUtc $absence.utc) -lt $finished) { throw 'Fresh cleanup evidence missing.' }
}

function Get-CampaignEvidence($Context) {
    $guns=$Context.case.panel -ceq 'guns'
    $resultPath = Join-Path $Context.run ('controller-results/' + $Context.case.jobId + '/result.json')
    $result = Read-CampaignJson $resultPath
    if ($result.id -cne $Context.case.jobId -or $result.panel -cne $Context.case.panel -or $result.language -cne $Context.case.language -or
        $result.action -cne 'verify-panel' -or $result.status -cne 'autonomous_captured_unreviewed' -or $result.failureCode -or
        -not (Test-CampaignTrue $result.cleanupVerified) -or -not (Test-CampaignTrue $result.configCleanupVerified) -or
        $result.acceptancePassed -isnot [bool] -or $result.acceptancePassed -or $result.rendererVerified -isnot [bool] -or $result.rendererVerified -or
        @($result.images).Count -ne 5) { throw 'Unaccepted client evidence.' }
    $interaction = $result.interaction
    $profile=if($guns){'guns-v299-1920x1200-v2'}else{'motd-v299-' + $Context.case.panel + '-1920x1200-v1'}
    $phases=if($guns){'exit-primary,select-primary,select-pistol,repeat-primary,exit-pistol-respawn'}else{'ok-first,controls,reopen,ok-last'}
    if ($interaction.profile -cne $profile -or
        ($interaction.actions.phase -join ',') -cne $phases -or
        @($interaction.actions | Where-Object { -not (Test-CampaignTrue $_.inputQueued) }).Count -or @($interaction.assessments).Count -ne 5) { throw 'Interaction evidence differs.' }
    $paths = @($resultPath)
    $expectedLabels = @(
        ($Context.case.panel + '-first'),'after-first-ok','after-controls',($Context.case.panel + '-alias'),'after-last-ok'
    )
    if($guns){$expectedLabels=@('primary-default','pistol-default','primary-selected','pistol-selected','after-respawn')}
    if (($result.labels -join ',') -cne ($expectedLabels -join ',')) { throw 'Frame labels differ.' }
    for ($i = 1; $i -le 5; $i++) {
        $frame = $result.images[$i - 1]
        $assessment = $interaction.assessments[$i - 1]
        if ($frame.number -ne $i -or $frame.format -cne 'bmp' -or
            $frame.width -ne 1920 -or $frame.height -ne 1200 -or $frame.bytes -lt 54 -or $frame.bytes -gt 16MB -or
            $assessment.number -ne $i) { throw 'Invalid frame metadata.' }
        $path = Join-Path ([IO.Path]::GetDirectoryName($resultPath)) "image-$i.bmp"
        if ((Get-CampaignHash $path) -cne $frame.sha256 -or (Get-Item -LiteralPath $path).Length -ne $frame.bytes) { throw 'Frame content differs.' }
        $header = [byte[]]::new(54)
        $stream = [IO.File]::OpenRead($path)
        try { if ($stream.Read($header, 0, 54) -ne 54) { throw 'Invalid bitmap.' } } finally { $stream.Dispose() }
        $offset = [BitConverter]::ToUInt32($header,10)
        $bits = [BitConverter]::ToUInt16($header,28)
        $rowBytes = [long][Math]::Floor((1920 * $bits + 31) / 32) * 4
        if ($header[0] -ne 66 -or $header[1] -ne 77 -or [BitConverter]::ToUInt32($header,2) -ne $frame.bytes -or
            [BitConverter]::ToUInt32($header,14) -ne 40 -or $offset -lt 54 -or $offset -gt 4096 -or
            $bits -notin @(24,32) -or [BitConverter]::ToUInt16($header,26) -ne 1 -or [BitConverter]::ToUInt32($header,30) -ne 0 -or
            [BitConverter]::ToInt32($header,18) -ne 1920 -or [Math]::Abs([long][BitConverter]::ToInt32($header,22)) -ne 1200 -or
            $offset + $rowBytes * 1200 -ne $frame.bytes) { throw 'Bitmap dimensions or pixels differ.' }
        $observation = $assessment.observation
        if($guns -and $i -le 4){
            $key=$expectedLabels[$i-1]
            if(-not (Test-CampaignTrue $observation.panelMatches) -or $observation.profile -cne $key -or
                $observation.bodySha256 -cne $Context.case.menuBaselines[$key]){throw 'Weapons baseline differs.'}
        }elseif (-not $guns -and $i -in @(1,4)) {
            if (-not (Test-CampaignTrue $observation.panelMatches) -or $observation.bodySha256 -cne $Context.case.baselineSha256) { throw 'Panel baseline differs.' }
        } elseif (-not (Test-CampaignTrue $observation.sceneCandidate) -or $observation.panelMatches -isnot [bool] -or $observation.panelMatches) { throw 'Closed scene missing.' }
        $paths += $path
    }
    $cleanupPath = Join-Path $Context.run 'cleanup.json'
    $cleanup = Read-CampaignJson $cleanupPath
    $errorsPath = Join-Path $Context.run 'evidence/amxx-errors.json'
    $errors = Read-CampaignJson $errorsPath
    $logPath = Join-Path $Context.run 'evidence/server-final.log'
    $null = Assert-LabOutputPath $logPath
    if ((Get-Item -LiteralPath $logPath).Length -gt 128KB) { throw 'Unbounded server evidence.' }
    $log = [IO.File]::ReadAllText($logPath)
    if($guns){
        if($result.readiness.status -cne 'ready' -or -not (Test-CampaignTrue $result.readiness.setupAcknowledged) -or
            $result.readiness.spawnSnapshot.sha256 -cnotmatch '\A[0-9A-F]{64}\z' -or $result.readiness.mode -cne 'native-fixed-v1'){throw 'Weapons readiness evidence missing.'}
        $expectedSetup='open-settings,open-languages,language-'+$Context.case.language+',close-player-menu,ack-'+$Context.case.language
        if(($result.readiness.setupActions.phase -join ',') -cne $expectedSetup -or
            @($result.readiness.setupActions|Where-Object {-not (Test-CampaignTrue $_.inputQueued) -or $_.frameSha256 -cne $result.readiness.spawnSnapshot.sha256}).Count -or
            $result.readiness.setupSnapshot.sha256 -cnotmatch '\A[0-9A-F]{64}\z' -or
            -not $log.Contains('FIXTURE_GUNS_READY phase=setup-ack language='+$Context.case.language)){throw 'Native setup acknowledgement differs.'}
        foreach($step in 1..3){if(-not $log.Contains("FIXTURE_GUNS_SETUP step=$step valid=1")){throw 'Native setup selection evidence missing.'}}
        $position=-1
        foreach($phase in @('connected','spawn','setup-ack')){
            $marker='FIXTURE_GUNS_READY phase='+$phase
            $next=$log.IndexOf($marker,[StringComparison]::Ordinal)
            if($next -le $position -or [regex]::Matches($log,[regex]::Escape($marker)).Count -ne 1){throw 'Ordered one-shot weapons readiness markers missing.'}
            $position=$next
        }
        foreach($step in 1..5){if(-not $log.Contains("FIXTURE_GUNS_SELECT step=$step inventory_unchanged=1")){throw 'Inventory continuity missing.'}}
        foreach($marker in @('FIXTURE_GUNS_START mp5=1 usp=1','FIXTURE_GUNS_DEATH requested=1','FIXTURE_GUNS_RESPAWN selected_loadout=1',
            'FIXTURE_GUNS_CONTROL step=1 respawn=0 movement=1','FIXTURE_GUNS_CONTROL step=3 respawn=0 movement=1',
            'FIXTURE_GUNS_CONTROL step=5 respawn=0 movement=1','FIXTURE_GUNS_CONTROL step=5 respawn=1 movement=1')){
            if(-not $log.Contains($marker)){throw 'Independent weapons marker missing.'}
        }
        if(@($interaction.checkpoints).Count -ne 4 -or ($interaction.checkpoints.name -join ',') -cne 'primary-exit,primary-reopened,pistol-selected-close,pistol-exit'){throw 'Weapons checkpoints differ.'}
        foreach($checkpoint in $interaction.checkpoints){
            $path=Join-Path ([IO.Path]::GetDirectoryName($resultPath)) ('checkpoint-'+$checkpoint.name+'.bmp')
            if((Get-CampaignHash $path) -cne $checkpoint.sha256 -or (Get-Item -LiteralPath $path).Length -ne $checkpoint.bytes -or $checkpoint.bytes -lt 54 -or $checkpoint.bytes -gt 16MB){throw 'Checkpoint integrity differs.'}
            $header=[byte[]]::new(54);$stream=[IO.File]::OpenRead($path)
            try{if($stream.Read($header,0,54) -ne 54){throw 'Checkpoint header missing.'}}finally{$stream.Dispose()}
            $offset=[BitConverter]::ToUInt32($header,10)
            if($header[0] -ne 66 -or $header[1] -ne 77 -or [BitConverter]::ToUInt32($header,2) -ne $checkpoint.bytes -or
                [BitConverter]::ToUInt32($header,14) -ne 40 -or $offset -lt 54 -or $offset -gt 4096 -or
                [BitConverter]::ToUInt16($header,26) -ne 1 -or [BitConverter]::ToUInt16($header,28) -ne 24 -or
                [BitConverter]::ToInt32($header,18) -ne 1920 -or [BitConverter]::ToInt32($header,22) -ne 1200 -or
                [BitConverter]::ToUInt32($header,30) -ne 0 -or $offset+6912000 -ne $checkpoint.bytes){throw 'Checkpoint bitmap differs.'}
            if($checkpoint.name -ceq 'primary-reopened'){
                if(-not (Test-CampaignTrue $checkpoint.observation.panelMatches) -or $checkpoint.observation.bodySha256 -cne $Context.case.menuBaselines['primary-default']){throw 'Reopened primary missing.'}
            }elseif(-not (Test-CampaignTrue $checkpoint.observation.sceneCandidate) -or $checkpoint.observation.panelMatches -isnot [bool] -or $checkpoint.observation.panelMatches){throw 'Closed menu checkpoint missing.'}
            $paths+=$path
        }
        $hashes=@($result.images[0].sha256,$interaction.checkpoints[1].sha256,$result.images[1].sha256,$result.images[2].sha256,$result.images[3].sha256)
        for($i=0;$i -lt 5;$i++){
            $action=$interaction.actions[$i]
            # Returned action bindings must reference the retained native frames.
            if($action.frameSha256 -cne $hashes[$i]){throw 'Weapons action binding differs.'}
        }
    }else{foreach ($epoch in @(1,2)) {
        $alias = $epoch - 1
        if (-not $log.Contains("FIXTURE_PANEL_COMMAND panel=$($Context.case.panel) epoch=$epoch team_alias=$alias cooldown_verified=$alias") -or
            -not $log.Contains("FIXTURE_PANEL_CONTROL panel=$($Context.case.panel) epoch=$epoch back_observed=1 left_observed=1 horizontal_distance_over_8=1")) { throw 'Independent control marker missing.' }
    }}
    if ($errors.errorFiles -isnot [long] -and $errors.errorFiles -isnot [int]) { throw 'Invalid error count.' }
    if ($errors.errorFiles -ne 0 -or $log -match 'Run time error|Load fails|bad load|Host_Error|FATAL ERROR' -or
        -not (Test-CampaignTrue $cleanup.fixtureRemoved) -or -not (Test-CampaignTrue $cleanup.fixtureEvidenceComplete)) { throw 'Fixture evidence incomplete.' }
    $absencePath = Join-Path $Context.run 'absence.json'
    $runnerPath = Join-Path $Context.run 'runner-after.json'
    Assert-CampaignCleanup $Context $result
    $paths += @($cleanupPath,$errorsPath,$logPath,$absencePath,$runnerPath)
    $files = @($paths | ForEach-Object { @{ name = [IO.Path]::GetRelativePath($Context.run,$_).Replace('\','/'); sha256 = Get-CampaignHash $_ } })
    $canonical = ($files | ForEach-Object { $_.name + ':' + $_.sha256 }) -join "`n"
    return @{ sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))); files = $files }
}

function Restore-CampaignEvidenceState($Context) {
    $path = Join-Path $Context.run 'campaign-evidence.json'
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    $evidence = Get-CampaignEvidence $Context
    if ((Read-CampaignJson $path).sha256 -cne $evidence.sha256) { throw 'Retained evidence changed.' }
    $Context.state.evidenceSha256 = $evidence.sha256
    $reviewPath = Join-Path $Context.run 'campaign-review.json'
    if (Test-Path -LiteralPath $reviewPath) {
        $review = Read-CampaignJson $reviewPath
        if ($review.jobId -cne $Context.case.jobId -or $review.evidenceSha256 -cne $evidence.sha256 -or
            -not (Test-CampaignTrue $review.accepted) -or $review.nativeFramesReviewed -ne 5) { throw 'Retained review changed.' }
        $Context.state.acceptancePassed = $true
        Save-CampaignState $Context 'accepted' ''
    } else { Save-CampaignState $Context 'captured_unreviewed' '' }
    return $true
}

function Invoke-LabCampaignCase([string]$Root, [string]$Panel, [string]$Language,
    [ValidateSet('run','resume')][string]$Operation, [scriptblock]$Port, [int]$PollLimit = 300) {
    $context = Get-CampaignCase $Root $Panel $Language
    Assert-CampaignPins $context
    $mutex = [Threading.Mutex]::new($false, 'Local\GoldSrcOps.ClientCampaign')
    $acquired = $false; $lock = $null
    try {
        try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Another campaign owns the controller.' }
        $lock = [IO.File]::Open((Join-Path $context.run 'campaign.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        if (Test-Path -LiteralPath (Join-Path $context.run 'campaign-evidence.json')) {
            $null = Restore-CampaignEvidenceState $context
            return $context.state
        }
        if ($context.state.phase -cin @('accepted','captured_unreviewed')) { throw 'Missing retained evidence.' }
        if ($context.state.phase -ceq 'terminal_unaccepted') { return $context.state }
        $runClaim = Join-Path $context.run 'run-claim.json'
        if ($Operation -ceq 'run') {
            if (Test-CampaignHeld $context) { throw 'Steam hold is active; no fixture or dispatch.' }
            if (Test-Path -LiteralPath (Join-Path $context.run 'cancel-request.json')) { throw 'Case was cancelled before dispatch.' }
            if (Test-Path -LiteralPath $runClaim) { throw 'Existing attempt: resume the same ID; never redispatch.' }
            $preflight = & $Port 'preflight' $context
            if (-not (Test-CampaignTrue $preflight.ready)) { throw 'Preflight unavailable; no attempt claimed.' }
            New-CampaignClaim $runClaim
        } elseif (-not (Test-Path -LiteralPath $runClaim)) { throw 'No existing attempt to resume.' }
        $stage = 'fixture'
        try {
            if ($Operation -ceq 'run') {
                Save-CampaignState $context 'starting' ''
                if ((Test-CampaignHeld $context) -or (Test-Path -LiteralPath (Join-Path $context.run 'cancel-request.json'))) { throw 'Held before fixture.' }
                New-CampaignClaim (Join-Path $context.run 'fixture-claim.json')
                $null = & $Port 'start' $context
                if ((Test-CampaignHeld $context) -or (Test-Path -LiteralPath (Join-Path $context.run 'cancel-request.json'))) { throw 'Held before relay.' }
                $null = & $Port 'relay' $context
                $stage = 'dispatch'
                # Serialize availability changes with the final check and one dispatch.
                $availabilityLock = [IO.File]::Open((Join-Path $Root 'availability.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                try {
                    if ((Test-CampaignHeld $context) -or (Test-Path -LiteralPath (Join-Path $context.run 'cancel-request.json'))) { throw 'Held before dispatch.' }
                    New-CampaignClaim (Join-Path $context.run 'dispatch-claim.json')
                    Save-CampaignState $context 'collecting' ''
                    $null = & $Port 'dispatch' $context
                } finally { $availabilityLock.Dispose() }
            }
            if (-not (Test-Path -LiteralPath (Join-Path $context.run 'dispatch-claim.json'))) { throw 'No dispatch claim to reconcile.' }
            $stage = 'collection'
            $terminal = $false
            $deadline = [Diagnostics.Stopwatch]::StartNew()
            for ($poll = 0; $poll -lt $PollLimit; $poll++) {
                if ($deadline.Elapsed.TotalSeconds -ge 600) { break }
                if ((Test-CampaignHeld $context) -or (Test-Path -LiteralPath (Join-Path $context.run 'cancel-request.json'))) { Send-CampaignCancel $context $Port }
                # A read error goes to reconciliation; it never sends cancellation.
                $result = & $Port 'result' $context
                if ($result.id -cne $context.case.jobId) { throw 'Result identity differs.' }
                if ($result.ContainsKey('completedUtc') -and $result.completedUtc) { $terminal = $true; break }
                $null = & $Port 'wait' $context
            }
            if (-not $terminal) { throw 'Collection deadline; reconcile the existing ID.' }
            if ($result.status -ceq 'autonomous_captured_unreviewed') { $null = & $Port 'images' $context }
            Save-CampaignState $context 'collected' ''
        } catch { Save-CampaignState $context 'needs_reconciliation' ($stage + '_unconfirmed') }
        finally {
            try { Stop-CampaignFixture $context $Port }
            catch { Save-CampaignState $context 'needs_reconciliation' 'fixture_cleanup_unconfirmed' }
        }
        if ($context.state.phase -ceq 'collected') {
            try {
                $runner = & $Port 'idle' $context
                Write-CampaignJson (Join-Path $context.run 'runner-after.json') $runner
                $raw = Read-CampaignJson (Join-Path $context.run ('controller-results/' + $context.case.jobId + '/result.json'))
                Assert-CampaignCleanup $context $raw
                if ($raw.status -cne 'autonomous_captured_unreviewed') {
                    Save-CampaignState $context 'terminal_unaccepted' 'client_did_not_capture'
                } else {
                    $evidence = Get-CampaignEvidence $context
                    $evidencePath = Join-Path $context.run 'campaign-evidence.json'
                    if (Test-Path -LiteralPath $evidencePath) {
                        if ((Read-CampaignJson $evidencePath).sha256 -cne $evidence.sha256) { throw 'Completed evidence identity changed.' }
                    } else { Write-CampaignJson $evidencePath $evidence -New }
                    $context.state.evidenceSha256 = $evidence.sha256
                    Save-CampaignState $context 'captured_unreviewed' ''
                }
            } catch { Save-CampaignState $context 'needs_reconciliation' 'evidence_unconfirmed' }
        }
        return $context.state
    } finally {
        if ($lock) { $lock.Dispose() }
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Request-LabCampaignCancel([string]$Root, [string]$Panel, [string]$Language, [scriptblock]$Port) {
    $context = Get-CampaignCase $Root $Panel $Language
    Assert-CampaignPins $context
    if ($context.state.phase -cin @('accepted','captured_unreviewed','terminal_unaccepted')) { return }
    $path = Join-Path $context.run 'cancel-request.json'
    if (-not (Test-Path -LiteralPath $path)) { New-CampaignClaim $path }
    if (Test-Path -LiteralPath (Join-Path $context.run 'dispatch-claim.json')) { Send-CampaignCancel $context $Port }
}

function Approve-LabCampaignCase([string]$Root, [string]$Panel, [string]$Language, [string]$EvidenceSha256, [bool]$ImagesReviewed) {
    $context = Get-CampaignCase $Root $Panel $Language
    Assert-CampaignPins $context
    $lock = [IO.File]::Open((Join-Path $context.run 'campaign.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        if (-not $ImagesReviewed -or $context.state.phase -cne 'captured_unreviewed' -or $EvidenceSha256 -cnotmatch '\A[0-9A-F]{64}\z') { throw 'Explicit five-frame review required.' }
        $evidence = Get-CampaignEvidence $context
        if ($evidence.sha256 -cne $EvidenceSha256 -or $evidence.sha256 -cne $context.state.evidenceSha256 -or
            (Read-CampaignJson (Join-Path $context.run 'campaign-evidence.json')).sha256 -cne $evidence.sha256) { throw 'Evidence changed after collection.' }
        Write-CampaignJson (Join-Path $context.run 'campaign-review.json') @{
            jobId = $context.case.jobId; evidenceSha256 = $evidence.sha256; nativeFramesReviewed = 5
            accepted = $true; rendererVerified = $false; utc = Get-CampaignUtc
        } -New
        $context.state.acceptancePassed = $true
        Save-CampaignState $context 'accepted' ''
        return $context.state
    } finally { $lock.Dispose() }
}

function Get-LabCampaignReport([string]$Root) {
    $campaign = Read-CampaignJson (Join-Path $Root 'campaign.json')
    $states = @($campaign.cases | ForEach-Object {
        $context = Get-CampaignCase $Root $_.panel $_.language
        $state = $context.state
        $accepted = $false
        $phase = $state.phase
        if ($state.phase -ceq 'accepted') {
            try {
                Assert-CampaignPins $context
                $evidence = Get-CampaignEvidence $context
                $review = Read-CampaignJson (Join-Path $context.run 'campaign-review.json')
                $accepted = (Test-CampaignTrue $state.acceptancePassed) -and (Test-CampaignTrue $review.accepted) -and
                    $review.jobId -ceq $context.case.jobId -and $review.nativeFramesReviewed -eq 5 -and
                    $review.evidenceSha256 -ceq $state.evidenceSha256 -and $evidence.sha256 -ceq $state.evidenceSha256
                if (-not $accepted) { $phase = 'integrity_unconfirmed' }
            } catch { $phase = 'integrity_unconfirmed' }
        }
        @{ panel = $context.case.panel; language = $context.case.language; jobId = $context.case.jobId; phase = $phase
            reason = $state.reason; evidenceSha256 = $state.evidenceSha256; acceptancePassed = $accepted }
    })
    return @{ id = $campaign.id; steamHeld = -not (Test-CampaignTrue (Read-CampaignJson (Join-Path $Root 'availability.json')).steamFree); cases = $states }
}

function Get-LabCampaignEvidence([string]$Root, [string]$Panel, [string]$Language) {
    $context = Get-CampaignCase $Root $Panel $Language
    # Inspection has no runtime port, state write, pin migration or acceptance effect.
    # Old source pins remain authoritative for every mutating campaign operation.
    Assert-CampaignPins $context -InputsOnly
    $sourceMatches = $true
    foreach ($pin in @($context.campaign.pins | Where-Object { $_.scope -ceq 'source' })) {
        if ((Get-CampaignHash (Join-Path $PSScriptRoot $pin.name)) -cne $pin.sha256) { $sourceMatches = $false }
    }
    $evidence = Get-CampaignEvidence $context
    return @{
        jobId = $context.case.jobId; panel = $Panel; language = $Language
        inputPinsVerified = $true; sourcePinsMatchCurrent = $sourceMatches
        verifierSha256 = Get-CampaignHash $PSCommandPath
        evidence = $evidence; acceptancePassed = $false; rendererVerified = $false
        readOnly = $true; runtimeChanged = $false
    }
}

Export-ModuleMember -Function Initialize-LabCampaign, Set-LabCampaignSteam, Invoke-LabCampaignCase, Request-LabCampaignCancel, Approve-LabCampaignCase, Get-LabCampaignReport, Get-LabCampaignEvidence
