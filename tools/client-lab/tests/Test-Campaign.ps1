#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Workspace, [string]$ToolRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $ToolRoot 'Lab.Campaign.psm1') -Force
$sandbox = Join-Path $Workspace ('campaign-checks-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $sandbox
$script:checks = 0
$script:effects = [Collections.Generic.List[string]]::new()
$script:mode = ''
function Check([bool]$Condition) { if (-not $Condition) { throw "Campaign assertion $script:checks failed at line $($MyInvocation.ScriptLineNumber)." }; $script:checks++ }
function Reject([scriptblock]$Action) { $failed = $false; try { $null = & $Action } catch { $failed = $true }; Check $failed }
function Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30)) }
function ReadJson([string]$Path) { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable }
$tokens = $null; $parseErrors = $null
$protocolAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $ToolRoot 'templates/code/Interaction.ps1.in'),[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw 'Runner protocol source does not parse.' }
$protocolFunctions = @($protocolAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -cin @('Get-LabInteractionPanel','Get-LabRunnerCaptureLabels')},$false))
if ($protocolFunctions.Count -ne 2) { throw 'Fixed runner label functions unavailable.' }
$protocolPath = Join-Path $sandbox 'RunnerLabels.ps1'
[IO.File]::WriteAllText($protocolPath, (($protocolFunctions | ForEach-Object { $_.Extent.Text }) -join "`n"))
. $protocolPath
function NewCase([switch]$Guns,[switch]$Renderer) {
    $root = Join-Path $sandbox ([guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'engine')
    [IO.File]::WriteAllText((Join-Path $root 'engine/pin.txt'), 'inert input')
    $cases = @()
    $panels=if($Guns){@('guns')}else{@('ranks','profile','topall')}
    foreach ($panel in $panels) {
        foreach ($language in @('ru','en')) {
            $key = "$panel-$language"
            $null = New-Item -ItemType Directory -Path (Join-Path $root "$key/evidence")
            [IO.File]::WriteAllText((Join-Path $root "$key/input.txt"), 'inert fixture')
            $case=@{ key = $key; panel = $panel; language = $language; baselineSha256 = ('A' * 64) }
            if($Guns){
                $case.menuBaselines=@{'primary-default'=('A'*64);'pistol-default'=('B'*64);'primary-selected'=('C'*64);'pistol-selected'=('D'*64)}
                $canonical=(@('primary-default','pistol-default','primary-selected','pistol-selected')|ForEach-Object {$_+':'+$case.menuBaselines[$_]}) -join "`n"
                $case.baselineSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
            }
            $cases += $case
        }
    }
    $policy=if($Renderer){'required-v1'}else{'legacy-ui'}
    Initialize-LabCampaign $root $cases @{ python = 'unused'; pythonSha256 = ('B' * 64); pwsh = 'unused' } -RendererPolicy $policy
    $script:effects.Clear()
    $script:mode = ''
    return $root
}

# Sparse synthetic bitmap with real dimensions and a complete pixel buffer.
$bitmap = Join-Path $sandbox 'synthetic.bmp'
$header = [byte[]]::new(54)
$header[0] = 66; $header[1] = 77
[BitConverter]::GetBytes([uint32]6912054).CopyTo($header,2)
[BitConverter]::GetBytes([uint32]54).CopyTo($header,10)
[BitConverter]::GetBytes([uint32]40).CopyTo($header,14)
[BitConverter]::GetBytes([int]1920).CopyTo($header,18)
[BitConverter]::GetBytes([int]1200).CopyTo($header,22)
[BitConverter]::GetBytes([uint16]1).CopyTo($header,26)
[BitConverter]::GetBytes([uint16]24).CopyTo($header,28)
$stream = [IO.File]::Create($bitmap)
try { $stream.SetLength(6912054); $stream.Write($header) } finally { $stream.Dispose() }
$bitmapHash = (Get-FileHash -LiteralPath $bitmap).Hash

function MakeResult($Context) {
    $directory = Join-Path $Context.run ('controller-results/' + $Context.case.jobId)
    $null = New-Item -ItemType Directory -Path $directory -Force
    $frames = @(); $assessments = @()
    for ($i = 1; $i -le 5; $i++) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory "image-$i.bmp"))) { Copy-Item -LiteralPath $bitmap -Destination (Join-Path $directory "image-$i.bmp") }
        $frames += @{ number = $i; format = 'bmp'; width = 1920; height = 1200; bytes = 6912054; sha256 = $bitmapHash }
        $panel = $i -in @(1,4)
        $assessments += @{ number = $i; observation = @{ panelMatches = $panel; sceneCandidate = -not $panel; bodySha256 = ('A' * 64) } }
    }
    $result = @{
        id = $Context.case.jobId; panel = $Context.case.panel; language = $Context.case.language; action = 'verify-panel'
        status = 'autonomous_captured_unreviewed'; failureCode = $null; cleanupVerified = $true; configCleanupVerified = $true
        acceptancePassed = $false; rendererVerified = $false; completedUtc = [DateTime]::UtcNow.AddSeconds(-5).ToString('o')
        images = $frames; labels = @(Get-LabRunnerCaptureLabels ([pscustomobject]@{action='verify-panel';panel=$Context.case.panel;language=$Context.case.language}))
        interaction = @{ profile = ('motd-v299-' + $Context.case.panel + '-1920x1200-v1'); assessments = $assessments
            actions = @(@{phase='ok-first';inputQueued=$true},@{phase='controls';inputQueued=$true},@{phase='reopen';inputQueued=$true},@{phase='ok-last';inputQueued=$true}) }
    }
    if($Context.case.panel -ceq 'guns'){
        $result.interaction.profile='guns-v299-1920x1200-v3'
        $result.readiness=@{status='ready';setupAcknowledged=$true;spawnSnapshot=@{sha256=$bitmapHash};setupSnapshot=@{sha256=$bitmapHash};setupActions=@()}
        foreach($phase in @('open-settings','open-languages',('language-'+$Context.case.language),'close-player-menu',('ack-'+$Context.case.language))){$result.readiness.setupActions+=@{phase=$phase;inputQueued=$true;frameSha256=$bitmapHash}}
        $result.readiness.mode='native-fixed-v2';$result.readiness.callback='gso_lab_ack'
        if($script:mode -ceq 'legacy-readiness'){$result.readiness.mode='native-fixed-v1'}
        if($script:mode -ceq 'wrong-callback'){$result.readiness.callback='gso_lab_setup_ack'}
        if($script:mode -ceq 'missing-readiness'){$result.readiness.setupAcknowledged=$false}
        $phases=@('exit-primary','select-primary','select-pistol','repeat-primary','exit-pistol-respawn')
        $result.interaction.actions=@($phases|ForEach-Object {@{phase=$_;inputQueued=$true;frameSha256=$bitmapHash}})
        for($i=0;$i -lt 4;$i++){
            $key=$result.labels[$i]
            $result.interaction.assessments[$i].observation=@{panelMatches=$true;sceneCandidate=$false;profile=$key;bodySha256=$Context.case.menuBaselines[$key]}
        }
        $result.interaction.assessments[4].observation=@{panelMatches=$false;sceneCandidate=$true;profile='scene';bodySha256=('E'*64)}
        $result.interaction.checkpoints=@()
        foreach($name in @('primary-exit','primary-reopened','pistol-selected-close','pistol-exit')){
            Copy-Item -LiteralPath $bitmap -Destination (Join-Path $directory ('checkpoint-'+$name+'.bmp'))
            $menu=$name -ceq 'primary-reopened'
            $result.interaction.checkpoints+=@{name=$name;sha256=$bitmapHash;bytes=6912054;observation=@{panelMatches=$menu;sceneCandidate=(-not $menu);bodySha256=$Context.case.menuBaselines['primary-default']}}
        }
        if($script:mode -ceq 'missing-checkpoint'){$result.interaction.checkpoints=@($result.interaction.checkpoints|Select-Object -First 3)}
        if($script:mode -ceq 'unbound-action'){$result.interaction.actions[0].frameSha256=('F'*64)}
    }
    if ($script:mode -ceq 'terminal') { $result.status = 'cancelled' }
    if ($script:mode -ceq 'bad-cleanup') { $result.cleanupVerified = 'true' }
    if ($script:mode -ceq 'duplicate-image') { $result.images[4].number = 1 }
    if ($script:mode -ceq 'wrong-baseline') { $result.interaction.assessments[0].observation.bodySha256 = ('F' * 64) }
    if ($script:mode -ceq 'missing-action') { $result.interaction.actions = @($result.interaction.actions | Select-Object -First 3) }
    if ($script:mode -ceq 'generic-labels') { $result.labels[0] = 'panel-first'; $result.labels[3] = 'panel-alias' }
    if ($script:mode -ceq 'foreign-panel-labels') { $result.labels[0] = 'rules-first'; $result.labels[3] = 'rules-alias' }
    if ($script:mode -ceq 'swapped-labels') { $result.labels[0] = $result.labels[3]; $result.labels[3] = 'after-last-ok' }
    if($Context.campaign.ContainsKey('rendererPolicy')){
        $result.gameSessionId=9;$result.gamePid=1234;$result.gameStartedUtc=[DateTime]::UtcNow.AddSeconds(-10).ToString('o')
        $nonce=([guid]$Context.case.jobId).ToString('N')
        $frame="GSO_RENDERER_BEGIN_$nonce`nGL Vendor: Fixture`nGL Renderer: Synthetic GPU`nGL Version: 4.6 test`nGSO_RENDERER_END_$nonce`n"
        $bytes=[Text.Encoding]::ASCII.GetBytes($frame)
        $result.rendererEvidence=@{schema=1;jobId=$Context.case.jobId;status='identified';reason='process_bound_gl_dump';probeEnabled=$true;cleanupVerified=$true;
            gamePid=1234;gameStartedUtc=$result.gameStartedUtc;observedUtc=[DateTime]::UtcNow.AddSeconds(-7).ToString('o');
            identity=@{backend='OpenGL';vendor='Fixture';renderer='Synthetic GPU';version='4.6 test'};frameBase64=[Convert]::ToBase64String($bytes);
            frameSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant();frameBytes=$bytes.Length}
        switch -CaseSensitive ($script:mode){
            renderer-missing {$null=$result.Remove('rendererEvidence')}
            renderer-unknown {$result.rendererEvidence.status='unconfirmed'}
            renderer-wrong-job {$result.rendererEvidence.jobId=[guid]::NewGuid().ToString('D')}
            renderer-wrong-pid {$result.rendererEvidence.gamePid=9999}
            renderer-wrong-start {$result.rendererEvidence.gameStartedUtc=[DateTime]::UtcNow.AddSeconds(-12).ToString('o')}
            renderer-wrong-frame {$result.rendererEvidence.frameSha256=('a'*64)}
            renderer-wrong-projection {$result.rendererEvidence.identity.vendor='Other'}
            renderer-bad-flag {$result.rendererEvidence.cleanupVerified='true'}
            renderer-dirty-log {$result.rendererEvidence.cleanupVerified=$false}
            renderer-stale {$result.rendererEvidence.observedUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('o')}
            renderer-future {$result.rendererEvidence.observedUtc=[DateTime]::UtcNow.AddMinutes(1).ToString('o')}
            renderer-missing-session {$null=$result.Remove('gameSessionId')}
            renderer-wrong-session {$result.gameSessionId=8}
        }
    }
    Json (Join-Path $directory 'result.json') $result
    return $result
}

$port = {
    param($action,$context)
    $script:effects.Add($action)
    switch -CaseSensitive ($action) {
        preflight { return @{ ready = ($script:mode -cne 'preflight') } }
        start {
            if ($script:mode -ceq 'start') { throw 'mock start interrupted' }
            if ($script:mode -ceq 'hold-during-start') { Set-LabCampaignSteam $context.root $false }
            return
        }
        relay { return }
        dispatch { if ($script:mode -ceq 'dispatch') { throw 'mock uncertain dispatch' }; return }
        result {
            if ($script:mode -ceq 'read-loss') { throw 'mock connection lost' }
            if ($script:mode -ceq 'pending') { return @{ id = $context.case.jobId; status = 'running' } }
            if ($script:mode -ceq 'hold-during-poll') {
                Set-LabCampaignSteam $context.root $false
                $script:mode = 'terminal'
                return @{ id = $context.case.jobId; status = 'running' }
            }
            return MakeResult $context
        }
        images { return }
        stop {
            if ($script:mode -ceq 'stop') { throw 'mock uncertain stop' }
            Json (Join-Path $context.run 'cleanup.json') @{ fixtureRemoved = $true; fixtureEvidenceComplete = $true }
            Json (Join-Path $context.run 'evidence/amxx-errors.json') @{ errorFiles = 0 }
            $log = @()
            foreach ($epoch in @(1,2)) {
                $alias = $epoch - 1
                $log += "FIXTURE_PANEL_COMMAND panel=$($context.case.panel) epoch=$epoch team_alias=$alias cooldown_verified=$alias"
                $log += "FIXTURE_PANEL_CONTROL panel=$($context.case.panel) epoch=$epoch back_observed=1 left_observed=1 horizontal_distance_over_8=1"
            }
            if($context.case.panel -ceq 'guns'){
                $log=@('FIXTURE_GUNS_READY phase=connected','FIXTURE_GUNS_READY phase=spawn',('FIXTURE_GUNS_READY phase=setup-ack language='+$context.case.language),'FIXTURE_GUNS_START mp5=1 usp=1','FIXTURE_GUNS_DEATH requested=1','FIXTURE_GUNS_RESPAWN selected_loadout=1',
                    'FIXTURE_GUNS_CONTROL step=1 respawn=0 movement=1','FIXTURE_GUNS_CONTROL step=3 respawn=0 movement=1',
                    'FIXTURE_GUNS_CONTROL step=5 respawn=0 movement=1','FIXTURE_GUNS_CONTROL step=5 respawn=1 movement=1')
                foreach($step in 1..3){$log+="L 01/01/2026 - 00:00:00: [fixture_vgui_guard.amxx] FIXTURE_GUNS_SETUP step=$step valid=1"}
                if($script:mode -ceq 'wrong-setup-language'){$log[2]='FIXTURE_GUNS_READY phase=setup-ack language=invalid'}
                if($script:mode -ceq 'ack-language-prefix'){$log[2]+='extra'}
                if($script:mode -ceq 'malformed-setup-flag'){$log[-1]=$log[-1].Replace('valid=1','valid=1677721600')}
                if($script:mode -ceq 'zero-setup-flag'){$log[-1]=$log[-1].Replace('valid=1','valid=0')}
                if($script:mode -ceq 'extra-setup-field'){$log[-1]+=' ignored=1'}
                if($script:mode -ceq 'missing-setup-step'){$log=@($log|Select-Object -SkipLast 1)}
                if($script:mode -ceq 'duplicate-setup-step'){$log+=@($log[-1])}
                if($script:mode -ceq 'reordered-setup'){$tmp=$log[-1];$log[-1]=$log[-2];$log[-2]=$tmp}
                foreach($step in 1..5){$log+="FIXTURE_GUNS_SELECT step=$step inventory_unchanged=1"}
            }
            if ($script:mode -ceq 'missing-marker') { $log = @($log | Select-Object -First 3) }
            [IO.File]::WriteAllText((Join-Path $context.run 'evidence/server-final.log'), ($log -join "`n"))
            return
        }
        absence { return @{ verified = ($script:mode -cne 'absence'); utc = [DateTime]::UtcNow.ToString('o') } }
        idle {
            $utc = if ($script:mode -ceq 'stale-heartbeat') { [DateTime]::UtcNow.AddMinutes(-5) } else { [DateTime]::UtcNow }
            return @{ worker = @{ state = 'ready';session=9;sessionMode='current-user-interactive'; client = @{ ready = $true; gameProcessCount = 0; utc = $utc.ToString('o') } } }
        }
        cancel { return }
        wait { return }
        default { throw 'Unexpected effect' }
    }
}

$root = NewCase
$before = @(Get-ChildItem -LiteralPath $root -Recurse -File).Count
$report = Get-LabCampaignReport $root
Check ($report.steamHeld -and $report.cases.Count -eq 6 -and @($report.cases | Where-Object acceptancePassed).Count -eq 0)
Check (@(Get-ChildItem -LiteralPath $root -Recurse -File).Count -eq $before)
Reject { Invoke-LabCampaignCase $root ranks en run $port }
Check ($script:effects.Count -eq 0)
Set-LabCampaignSteam $root $true
$script:mode = 'preflight'
Reject { Invoke-LabCampaignCase $root ranks en run $port }
Check (-not (Test-Path -LiteralPath (Join-Path $root 'ranks-en/run-claim.json')))
$script:mode = ''
$state = Invoke-LabCampaignCase $root ranks en run $port
Check ($state.phase -ceq 'captured_unreviewed' -and -not $state.acceptancePassed)
Check (@($script:effects | Where-Object { $_ -ceq 'dispatch' }).Count -eq 1)
Reject { Approve-LabCampaignCase $root ranks en $state.evidenceSha256 $false }
Reject { Approve-LabCampaignCase $root ranks en ('B' * 64) $true }
$accepted = Approve-LabCampaignCase $root ranks en $state.evidenceSha256 $true
Check ($accepted.acceptancePassed -and $accepted.phase -ceq 'accepted')
$effectCount = $script:effects.Count
$null = Invoke-LabCampaignCase $root ranks en resume $port
Check ($script:effects.Count -eq $effectCount)
$raw = ReadJson (Join-Path $root ('ranks-en/controller-results/' + $state.jobId + '/result.json'))
Check (-not $raw.acceptancePassed -and -not $raw.rendererVerified)
Check (@((Get-LabCampaignReport $root).cases | Where-Object acceptancePassed).Count -eq 1)
$savedState = ReadJson (Join-Path $root 'ranks-en/campaign-state.json')
$savedState.phase = 'collected'; $savedState.acceptancePassed = $false
Json (Join-Path $root 'ranks-en/campaign-state.json') $savedState
$effectCount = $script:effects.Count
$restored = Invoke-LabCampaignCase $root ranks en resume $port
Check ($restored.phase -ceq 'accepted' -and $restored.acceptancePassed -and $script:effects.Count -eq $effectCount)
[IO.File]::AppendAllText((Join-Path $root 'ranks-en/evidence/server-final.log'), "`nchanged")
Check (@((Get-LabCampaignReport $root).cases | Where-Object acceptancePassed).Count -eq 0)
Reject { Invoke-LabCampaignCase $root ranks en resume $port }

foreach ($scenario in @('start','dispatch','read-loss','pending','stop','absence','stale-heartbeat','bad-cleanup','duplicate-image','wrong-baseline','missing-action','missing-marker','hold-during-start','generic-labels','foreign-panel-labels','swapped-labels')) {
    $root = NewCase
    Set-LabCampaignSteam $root $true
    $script:mode = $scenario
    $state = Invoke-LabCampaignCase $root ranks ru run $port -PollLimit 2
    Check ($state.phase -ceq 'needs_reconciliation' -and -not $state.acceptancePassed)
    Check (@($script:effects | Where-Object { $_ -ceq 'stop' }).Count -eq 1)
    if ($scenario -cin @('read-loss','pending')) { Check (-not $script:effects.Contains('cancel')) }
    $effectCount = $script:effects.Count
    Reject { Invoke-LabCampaignCase $root ranks ru run $port }
    Check ($script:effects.Count -eq $effectCount)
}

# Same-ID reconciliation after uncertain delivery: reads and image recovery only.
$root = NewCase
Set-LabCampaignSteam $root $true
$script:mode = 'dispatch'
$state = Invoke-LabCampaignCase $root profile en run $port
$jobId = $state.jobId
$script:effects.Clear(); $script:mode = ''
$state = Invoke-LabCampaignCase $root profile en resume $port
Check ($state.jobId -ceq $jobId -and $state.phase -ceq 'captured_unreviewed')
Check (-not $script:effects.Contains('start') -and -not $script:effects.Contains('relay') -and -not $script:effects.Contains('dispatch') -and -not $script:effects.Contains('stop'))

# A stop claim is never retried even if the first call failed.
$root = NewCase
Set-LabCampaignSteam $root $true
$script:mode = 'stop'
$null = Invoke-LabCampaignCase $root profile ru run $port
$script:effects.Clear(); $script:mode = ''
$state = Invoke-LabCampaignCase $root profile ru resume $port
Check ($state.phase -ceq 'needs_reconciliation' -and -not $script:effects.Contains('stop'))

$root = NewCase
Request-LabCampaignCancel $root ranks en $port
Reject { Invoke-LabCampaignCase $root ranks en run $port }
Check ($script:effects.Count -eq 0)

$root = NewCase
Set-LabCampaignSteam $root $true
$script:mode = 'hold-during-poll'
$state = Invoke-LabCampaignCase $root topall en run $port -PollLimit 3
Check ($state.phase -ceq 'terminal_unaccepted' -and @($script:effects | Where-Object { $_ -ceq 'cancel' }).Count -eq 1)
Request-LabCampaignCancel $root topall en $port
Check (@($script:effects | Where-Object { $_ -ceq 'cancel' }).Count -eq 1)
Check ((Get-LabCampaignReport $root).steamHeld)

# Frame tampering after complete collection invalidates a visual approval.
$root = NewCase
Set-LabCampaignSteam $root $true
$state = Invoke-LabCampaignCase $root topall ru run $port
$path = Join-Path $root ('topall-ru/controller-results/' + $state.jobId + '/image-1.bmp')
$stream = [IO.File]::OpenWrite($path)
try { $stream.Position = 60; $stream.WriteByte(1) } finally { $stream.Dispose() }
Reject { Approve-LabCampaignCase $root topall ru $state.evidenceSha256 $true }
Check (-not (ReadJson (Join-Path $root 'topall-ru/campaign-state.json')).acceptancePassed)

$root = NewCase
Set-LabCampaignSteam $root $true
[IO.File]::WriteAllText((Join-Path $root 'ranks-ru/input.txt'), 'changed')
Reject { Invoke-LabCampaignCase $root ranks ru run $port }
Check ($script:effects.Count -eq 0)

$root = NewCase
Set-LabCampaignSteam $root $true
$lock = [IO.File]::Open((Join-Path $root 'profile-en/campaign.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try { Reject { Invoke-LabCampaignCase $root profile en run $port } } finally { $lock.Dispose() }
Check ($script:effects.Count -eq 0)

# Use actual runner label generation for every panel/language pair.
$root = NewCase
Set-LabCampaignSteam $root $true
foreach ($panelName in @('ranks','profile','topall')) {
    foreach ($languageName in @('ru','en')) {
        $state = Invoke-LabCampaignCase $root $panelName $languageName run $port
        Check ($state.phase -ceq 'captured_unreviewed' -and -not $state.acceptancePassed)
    }
}

# A corrected verifier may inspect old evidence, but cannot migrate or accept it.
$campaignPath = Join-Path $root 'campaign.json'
$campaign = ReadJson $campaignPath
($campaign.pins | Where-Object { $_.scope -ceq 'source' -and $_.name -ceq 'Lab.Campaign.psm1' }).sha256 = ('F' * 64)
Json $campaignPath $campaign
$before = @{}
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) { $before[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
$script:effects.Clear()
$inspection = Get-LabCampaignEvidence $root ranks en
Check ($inspection.readOnly -and $inspection.inputPinsVerified -and -not $inspection.sourcePinsMatchCurrent -and -not $inspection.acceptancePassed)
Check ($script:effects.Count -eq 0)
Check (@(Get-ChildItem -LiteralPath $root -Recurse -File).Count -eq $before.Count)
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) { if ((Get-FileHash -LiteralPath $file.FullName).Hash -cne $before[$file.FullName]) { throw 'Read-only inspection changed evidence.' } }
Check $true
Reject { Invoke-LabCampaignCase $root ranks en resume $port }
Reject { Approve-LabCampaignCase $root ranks en $inspection.evidence.sha256 $true }
$cliInspection = & (Join-Path $ToolRoot 'Invoke-LabCampaign.ps1') -Operation verify-evidence -CampaignDirectory $root -Panel ranks -Language en | ConvertFrom-Json
Check ($cliInspection.evidence.sha256 -ceq $inspection.evidence.sha256 -and -not $cliInspection.runtimeChanged)
[IO.File]::WriteAllText((Join-Path $root 'ranks-en/input.txt'), 'changed after capture')
Reject { Get-LabCampaignEvidence $root ranks en }

# Public CLI planning is inert and does not create missing campaign state.
foreach($language in @('ru','en')){
    $root=NewCase -Guns
    Check ((Get-LabCampaignReport $root).cases.Count -eq 2)
    Set-LabCampaignSteam $root $true
    $state=Invoke-LabCampaignCase $root guns $language run $port
    Check ($state.phase -ceq 'captured_unreviewed')
    $accepted=Approve-LabCampaignCase $root guns $language $state.evidenceSha256 $true
    Check ($accepted.phase -ceq 'accepted' -and $accepted.acceptancePassed)
}
foreach($failureMode in @('legacy-readiness','wrong-callback','ack-language-prefix','malformed-setup-flag','zero-setup-flag','extra-setup-field','missing-setup-step','duplicate-setup-step','reordered-setup','wrong-setup-language','missing-readiness','missing-marker','missing-checkpoint','unbound-action','wrong-baseline','stale-heartbeat','bad-cleanup')){
    $root=NewCase -Guns;Set-LabCampaignSteam $root $true;$script:mode=$failureMode
    $state=Invoke-LabCampaignCase $root guns en run $port
    Check ($state.phase -ceq 'needs_reconciliation' -and -not $state.acceptancePassed)
    Check (@($script:effects|Where-Object {$_ -ceq 'dispatch'}).Count -eq 1)
}
# Renderer-required campaigns use the same parser as the standalone reviewer.
$root=NewCase -Renderer;Set-LabCampaignSteam $root $true
$state=Invoke-LabCampaignCase $root profile en run $port
Check ($state.phase -ceq 'captured_unreviewed')
$caseReport=(Get-LabCampaignReport $root).cases|Where-Object {$_.panel -ceq 'profile' -and $_.language -ceq 'en'}
Check ($caseReport.renderer.required -and $caseReport.renderer.identified -and $caseReport.cleanupVerified -and -not $caseReport.uiAccepted)
Check ($caseReport.renderer.gameSessionId -eq 9 -and $caseReport.renderer.workerSessionId -eq 9)
$rendererPath=Join-Path $root 'profile-en/campaign-renderer.json'
$receiptHash=(Get-FileHash $rendererPath).Hash
$rawPath=Join-Path $root ('profile-en/controller-results/'+$state.jobId+'/result.json')
$rawHash=(Get-FileHash $rawPath).Hash
$accepted=Approve-LabCampaignCase $root profile en $state.evidenceSha256 $true
Check ($accepted.phase -ceq 'accepted')
$report=(Get-LabCampaignReport $root).cases|Where-Object {$_.panel -ceq 'profile' -and $_.language -ceq 'en'}
Check ($report.acceptancePassed -and $report.uiAccepted -and $report.renderer.identified -and $report.cleanupVerified)
Check ((Get-FileHash $rawPath).Hash -ceq $rawHash)
$script:effects.Clear();$null=Invoke-LabCampaignCase $root profile en resume $port
Check ($script:effects.Count -eq 0 -and (Get-FileHash $rendererPath).Hash -ceq $receiptHash)
$reviewPath=Join-Path $root 'profile-en/campaign-review.json'
$review=ReadJson $reviewPath;$review.rendererReceiptSha256=('F'*64);Json $reviewPath $review
Check (-not @((Get-LabCampaignReport $root).cases|Where-Object acceptancePassed).Count)
Reject {Invoke-LabCampaignCase $root profile en resume $port}
$review.rendererReceiptSha256=$receiptHash;Json $reviewPath $review
$rendererReceipt=ReadJson $rendererPath;$rendererReceipt.identity.vendor='Changed';Json $rendererPath $rendererReceipt
Check (-not @((Get-LabCampaignReport $root).cases|Where-Object acceptancePassed).Count)
Reject {Get-LabCampaignEvidence $root profile en}
Remove-Item -LiteralPath $rendererPath
Reject {Invoke-LabCampaignCase $root profile en resume $port}
Check (-not (Test-Path $rendererPath) -and $script:effects.Count -eq 0)
foreach($rendererMode in @('renderer-missing','renderer-unknown','renderer-wrong-job','renderer-wrong-pid','renderer-wrong-start','renderer-wrong-frame',
    'renderer-wrong-projection','renderer-bad-flag','renderer-dirty-log','renderer-stale','renderer-future','renderer-missing-session','renderer-wrong-session')){
    $root=NewCase -Renderer;Set-LabCampaignSteam $root $true;$script:mode=$rendererMode
    $state=Invoke-LabCampaignCase $root profile en run $port
    Check ($state.phase -ceq 'needs_reconciliation' -and -not $state.acceptancePassed)
    Check (@($script:effects|Where-Object {$_ -ceq 'dispatch'}).Count -eq 1)
    $script:effects.Clear();Reject {Invoke-LabCampaignCase $root profile en run $port};Check ($script:effects.Count -eq 0)
}
$root=NewCase -Renderer;$manifestPath=Join-Path $root 'campaign.json';$manifest=ReadJson $manifestPath
$manifest.pins=@($manifest.pins|Where-Object {$_.name -cne 'Lab.RendererReview.psm1'});Json $manifestPath $manifest
Reject {Invoke-LabCampaignCase $root profile en run $port}
Check ($script:effects.Count -eq 0)
$manifest.rendererPolicy='unknown';Json $manifestPath $manifest
Reject {Get-LabCampaignReport $root}

$missing = Join-Path $sandbox 'missing'
$plan = & (Join-Path $ToolRoot 'Invoke-LabCampaign.ps1') -Operation run -CampaignDirectory $missing -Panel ranks -Language en | ConvertFrom-Json
Check (-not $plan.execute -and -not (Test-Path -LiteralPath $missing))
$summary = @{ checks = $script:checks; targetNetworkUsed = $false; runtimeChanged = $false; nativeInputSent = $false; passed = $true }
Json (Join-Path $Workspace 'campaign-summary.json') $summary
$summary | ConvertTo-Json
