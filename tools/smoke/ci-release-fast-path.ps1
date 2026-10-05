#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$scopeScript = Join-Path $repoRoot "tools/ci/resolve-change-scope.ps1"
$documentationScript = Join-Path $repoRoot "tools/ci/test-changed-documentation.ps1"
$workflowPath = Join-Path $repoRoot ".github/workflows/ci.yml"
$runId = [Guid]::NewGuid().ToString("N")
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-ci-fast-path-$runId"

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Scope {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string[]]$Paths,

        [Parameter(Mandatory = $true)]
        [bool]$ExpectedDocsOnly,

        [bool]$ExpectedStablePromotion = $false,

        [bool]$ExpectedAddonOnly = $false
    )

    $result = & $scopeScript -EventName pull_request -ChangedPath $Paths
    Assert-Condition `
        -Condition ($result.DocsOnly -eq $ExpectedDocsOnly) `
        -Message "Change-scope case '$Name' returned '$($result.Mode)'."
    Assert-Condition `
        -Condition ($result.StablePromotion -eq $ExpectedStablePromotion) `
        -Message "Change-scope case '$Name' returned unexpected stable-promotion state '$($result.StablePromotion)'."
    Assert-Condition `
        -Condition ($result.AddonOnly -eq $ExpectedAddonOnly) `
        -Message "Change-scope case '$Name' returned unexpected addon-only state '$($result.AddonOnly)'."
}

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessage,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -like $ExpectedMessage) {
            return
        }
        throw "Expected error '$ExpectedMessage', got '$($_.Exception.Message)'."
    }

    throw "Expected error '$ExpectedMessage', but the action succeeded."
}

function Get-WorkflowJobBlock {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Workflow,

        [Parameter(Mandatory = $true)]
        [string]$JobName
    )

    $pattern = "(?ms)^  $([regex]::Escape($JobName)):\r?\n(?<body>.*?)(?=^  [a-z0-9][a-z0-9-]*:\r?$|\z)"
    $match = [regex]::Match($Workflow, $pattern)
    Assert-Condition -Condition $match.Success -Message "Workflow job '$JobName' was not found."
    return $match.Value
}

function Invoke-TestGit {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = @(& git @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Test Git command failed: git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    return $output
}

Assert-Scope -Name "Markdown only" -Paths @("README.md", "docs/deployment.md") -ExpectedDocsOnly $true
Assert-Scope -Name "License only" -Paths @("LICENSE") -ExpectedDocsOnly $true
Assert-Scope -Name "Workflow mixed with docs" -Paths @("docs/deployment.md", ".github/workflows/ci.yml") -ExpectedDocsOnly $false
Assert-Scope -Name "Source mixed with docs" -Paths @("README.md", "src/GoldSrcOps.Api/Program.cs") -ExpectedDocsOnly $false

$addonSource = "samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma"
$mapSource = "samples/amxmodx-map-menu/goldsrcops_map_menu.sma"
foreach ($path in @(
        $addonSource, $mapSource,
        "samples/amxmodx-weapon-selection/goldsrcops-weapon-selection.cfg",
        "samples/amxmodx-weapon-selection/goldsrcops-player-menu.txt",
        "samples/amxmodx-map-menu/goldsrcops-map-menu.cfg",
        "samples/amxmodx-map-menu/goldsrcops-map-menu.txt",
        "tools/smoke/amxx-map-menu.ps1", "tools/smoke/amxx-map-menu.sma",
        "tools/smoke/amxx-map-language-provider.sma", "tools/smoke/amxx-weapon-selection.ps1",
        "tools/smoke/amxx-spawn-loadout.sma", "tools/smoke/amxx-map-stats.sma",
        "tools/smoke/amxx-persistent-stats.sma", "tools/smoke/amxx-player-menu.sma",
        "tools/smoke/amxx-player-preferences.sma", "tools/smoke/player-menu-dictionary.ps1",
        "tools/smoke/weapon-selection-package.ps1", "tools/smoke/gameserver-weapon-selection-addon.sh",
        "tools/smoke/game-kill-rewards-runtime.sh", "tools/smoke/game-map-menu-runtime.sh",
        "tools/smoke/game-player-preferences-runtime.sh",
        "tools/release/build-map-menu-addon.ps1", "tools/release/build-weapon-selection-sandbox.ps1",
        "ops/gameserver/weapon-selection-addon.sh"
    )) {
    Assert-Scope -Name "Addon input $path" -Paths @($path, "docs/backlog.md") -ExpectedDocsOnly $false -ExpectedAddonOnly $true
}
foreach ($path in @(
        ".github/workflows/ci.yml", "tools/ci/resolve-change-scope.ps1",
        "tools/ci/test-changed-documentation.ps1", "tools/smoke/ci-release-fast-path.ps1",
        "tools/smoke/amxx-game-event-producer.ps1", "tools/smoke/unknown-runtime.sh",
        "samples/amxmodx-game-event-producer/goldsrcops_game_events.sma",
        "samples/amxmodx-weapon-selection/unknown.sma", "samples/amxmodx-map-menu/extra.cfg",
        "ops/gameserver/game-event-persistent.sh", "ops/production/compose.yml",
        "src/GoldSrcOps.Api/Program.cs", "tests/GoldSrcOps.WebTests/Example.cs",
        "Directory.Build.props", "global.json", "Dockerfile", "unknown.txt",
        "../$addonSource", "/$addonSource", "samples/../$addonSource", $addonSource.ToUpperInvariant()
    )) {
    Assert-Scope -Name "Mixed or unknown $path" -Paths @($addonSource, $path) -ExpectedDocsOnly $false
}

$addonPush = & $scopeScript -EventName push -Ref "refs/heads/main" -ChangedPath @($addonSource)
Assert-Condition -Condition ($addonPush.Mode -eq "addon-only") -Message "A main push with only addon paths must select addon-only."
foreach ($event in @(
        @{ EventName = "workflow_dispatch" },
        @{ EventName = "push"; Ref = "refs/tags/v9.9.9-rc.1" },
        @{ EventName = "push"; Ref = "refs/heads/feature/example" }
    )) {
    $result = & $scopeScript @event -ChangedPath @($addonSource)
    Assert-Condition -Condition ($result.Mode -eq "full") -Message "Manual, candidate and unsupported events must retain full gates."
}
$missingRevision = & $scopeScript -EventName pull_request -BaseRevision ('0' * 40) -HeadRevision ('1' * 40)
Assert-Condition -Condition ($missingRevision.Mode -eq "full") -Message "An unresolved revision must retain full gates."

$emptyResult = & $scopeScript -EventName pull_request -ChangedPath @()
Assert-Condition -Condition (-not $emptyResult.DocsOnly) -Message "An empty diff was accepted as documentation-only."
Assert-Condition -Condition ($emptyResult.Mode -eq "full") -Message "An empty diff must retain full gates."

$tagResult = & $scopeScript `
    -EventName push `
    -Ref "refs/tags/v9.9.9" `
    -ChangedPath @("README.md")
Assert-Condition -Condition (-not $tagResult.DocsOnly) -Message "A release tag was accepted as documentation-only."
Assert-Condition -Condition $tagResult.StablePromotion -Message "A stable release tag did not select the promotion fast path."
Assert-Condition -Condition ($tagResult.Mode -eq "stable-promotion") -Message "A stable release tag returned mode '$($tagResult.Mode)'."

$candidateTagResult = & $scopeScript `
    -EventName push `
    -Ref "refs/tags/v9.9.9-rc.1" `
    -ChangedPath @("README.md")
Assert-Condition -Condition (-not $candidateTagResult.DocsOnly) -Message "A candidate tag was accepted as documentation-only."
Assert-Condition -Condition (-not $candidateTagResult.StablePromotion) -Message "A candidate tag selected the stable-promotion fast path."
Assert-Condition -Condition ($candidateTagResult.Mode -eq "full") -Message "A candidate tag returned mode '$($candidateTagResult.Mode)'."

$invalidStableTagResult = & $scopeScript `
    -EventName push `
    -Ref "refs/tags/v09.9.9" `
    -ChangedPath @("README.md")
Assert-Condition -Condition (-not $invalidStableTagResult.StablePromotion) -Message "A non-canonical stable tag selected the promotion fast path."

$unresolvedPushResult = & $scopeScript -EventName push -ChangedPath @("README.md")
Assert-Condition -Condition (-not $unresolvedPushResult.DocsOnly) -Message "A push without a ref was accepted as documentation-only."
Assert-Condition -Condition (-not $unresolvedPushResult.StablePromotion) -Message "A push without a ref selected the stable-promotion fast path."

$featurePushResult = & $scopeScript `
    -EventName push `
    -Ref "refs/heads/feature/example" `
    -ChangedPath @("README.md")
Assert-Condition -Condition (-not $featurePushResult.DocsOnly) -Message "An unsupported branch push was accepted as documentation-only."
Assert-Condition -Condition (-not $featurePushResult.StablePromotion) -Message "An unsupported branch push selected the stable-promotion fast path."

$manualResult = & $scopeScript -EventName workflow_dispatch -ChangedPath @("README.md")
Assert-Condition -Condition (-not $manualResult.DocsOnly) -Message "A manual run was accepted as documentation-only."
Assert-Condition -Condition (-not $manualResult.StablePromotion) -Message "A manual run selected the stable-promotion fast path."

$workflow = [IO.File]::ReadAllText($workflowPath)
$triggerBlock = ($workflow -split '(?m)^permissions:\s*$', 2)[0]
Assert-Condition `
    -Condition ($triggerBlock -match '(?ms)^  push:\r?\n    branches:\r?\n      - main\r?\n    tags:\r?\n      - "v\*"') `
    -Message "The CI push trigger must be limited to main and release tags."
Assert-Condition `
    -Condition ($triggerBlock -match '(?ms)^  pull_request:\r?\n    branches:\r?\n      - main') `
    -Message "The CI pull-request trigger must target main."
Assert-Condition `
    -Condition ($workflow -match '(?ms)^concurrency:\r?\n  group: \$\{\{ github\.workflow \}\}-\$\{\{ github\.event\.pull_request\.number \|\| github\.run_id \}\}\r?\n  cancel-in-progress: \$\{\{ github\.event_name == ''pull_request'' \}\}') `
    -Message "The PR-only concurrency contract changed."

$scopeJob = Get-WorkflowJobBlock -Workflow $workflow -JobName "change-scope"
$qualityJob = Get-WorkflowJobBlock -Workflow $workflow -JobName "quality"
$containerJob = Get-WorkflowJobBlock -Workflow $workflow -JobName "container-smoke"
$browserJob = Get-WorkflowJobBlock -Workflow $workflow -JobName "browser-smoke"
$compileJob = Get-WorkflowJobBlock -Workflow $workflow -JobName "map-menu-compile"
Assert-Condition `
    -Condition ($scopeJob -match '(?m)^      stable_promotion: \$\{\{ steps\.scope\.outputs\.stable_promotion \}\}$') `
    -Message "Change Scope does not expose the stable-promotion decision."
Assert-Condition `
    -Condition ($scopeJob -match '(?m)^      addon_only: \$\{\{ steps\.scope\.outputs\.addon_only \}\}$') `
    -Message "Change Scope does not expose the addon-only decision."
Assert-Condition `
    -Condition ($qualityJob -match '(?m)^    if: \$\{\{ !cancelled\(\) \}\}$') `
    -Message "Quality Gate must fail closed after scope errors without surviving workflow cancellation."
foreach ($job in @($scopeJob, $qualityJob)) {
    Assert-Condition `
        -Condition ($job -match '(?m)^      - name: Checkout\r?\n        uses: actions/checkout@v6') `
        -Message "Change Scope and Quality Gate must always check out the repository."
}
Assert-Condition `
    -Condition ($qualityJob -match 'Confirm stable-promotion fast path') `
    -Message "Quality Gate lacks an explicit stable-promotion result."
foreach ($job in @($containerJob, $browserJob)) {
    Assert-Condition `
        -Condition ($job -match '(?m)^      - name: Checkout\r?\n        if: needs\.change-scope\.outputs\.docs_only != ''true'' && needs\.change-scope\.outputs\.stable_promotion != ''true''(?: && needs\.change-scope\.outputs\.addon_only != ''true'')?\r?\n        uses: actions/checkout@v6') `
        -Message "Runtime checkout must use explicit fast-path decisions."
    Assert-Condition `
        -Condition ($job -match 'Confirm documentation-only fast path') `
        -Message "A required runtime job lacks an explicit documentation-only result."
    Assert-Condition `
        -Condition ($job -match 'Confirm stable-promotion fast path') `
        -Message "A required runtime job lacks an explicit stable-promotion result."
}
Assert-Condition `
    -Condition ($workflow -notmatch '(?m)^        if: needs\.change-scope\.outputs\.docs_only != ''true''$') `
    -Message "A full-gate step can still run during stable promotion."

function Test-WorkflowCondition {
    param(
        [string]$Condition,
        [hashtable]$Context
    )

    if ([string]::IsNullOrWhiteSpace($Condition)) { return $true }
    $expression = $Condition.Trim().Replace('${{', '').Replace('}}', '').Trim()
    foreach ($key in $Context.Keys) {
        $expression = $expression.Replace($key, "'$($Context[$key])'")
    }
    $expression = $expression.Replace('!cancelled()', '$true')
    $expression = $expression.Replace('!=', '-cne').Replace('==', '-ceq').Replace('&&', '-and').Replace('||', '-or')
    # Evaluate only the small comparison grammar used by these workflow gates.
    $unrecognized = [regex]::Replace($expression, "'[^']*'|\`$true|-cne|-ceq|-and|-or|[\s()]", '')
    if ($unrecognized.Length -gt 0) { throw "Unsupported workflow condition: $Condition" }
    return [bool](& ([scriptblock]::Create($expression)))
}

function Get-WorkflowSteps {
    param([string]$Job)

    foreach ($match in [regex]::Matches($Job, '(?ms)^      - name: (?<name>[^\r\n]+)\r?\n(?<body>.*?)(?=^      - name: |\z)')) {
        $condition = [regex]::Match($match.Groups['body'].Value, '(?m)^        if: (?<condition>[^\r\n]+)').Groups['condition'].Value
        [pscustomobject]@{ Name = $match.Groups['name'].Value; Condition = $condition; Body = $match.Groups['body'].Value }
    }
}

$jobs = @{ quality = $qualityJob; container = $containerJob; browser = $browserJob; compile = $compileJob }
$addonSteps = @{
    quality = @('Checkout', 'Validate CI release fast path', 'Confirm addon-only fast path', 'Validate changed documentation')
    container = @('Confirm addon-only fast path', 'Checkout', 'Validate weapon-selection addon installation, upgrade and toggle', 'Validate gameplay runtime script syntax', 'Validate map-menu addon installation, removal and rollback')
    browser = @('Confirm addon-only fast path')
    compile = @('Checkout', 'Compile map menu and isolated fixture with pinned toolchain', 'Compile weapon addon and gameplay fixtures, verify package')
}
foreach ($mode in @('addon-only', 'docs-only', 'stable-promotion', 'full')) {
    $context = @{
        'needs.change-scope.outputs.docs_only' = ($mode -eq 'docs-only').ToString().ToLowerInvariant()
        'needs.change-scope.outputs.addon_only' = ($mode -eq 'addon-only').ToString().ToLowerInvariant()
        'needs.change-scope.outputs.stable_promotion' = ($mode -eq 'stable-promotion').ToString().ToLowerInvariant()
        'needs.change-scope.result' = 'success'
        'needs.quality.result' = 'success'
        'needs.map-menu-compile.result' = 'success'
    }
    foreach ($jobName in $jobs.Keys) {
        $steps = @(Get-WorkflowSteps -Job $jobs[$jobName])
        $active = @($steps | Where-Object { Test-WorkflowCondition -Condition $_.Condition -Context $context } | ForEach-Object Name)
        if ($mode -eq 'addon-only') {
            Assert-Condition -Condition (($active -join '|') -ceq ($addonSteps[$jobName] -join '|')) `
                -Message "Unexpected addon-only steps in ${jobName}: $($active -join ', ')"
        }
        elseif ($mode -in @('docs-only', 'stable-promotion')) {
            $confirmation = if ($mode -eq 'docs-only') { 'Confirm documentation-only fast path' } else { 'Confirm stable-promotion fast path' }
            $expected = switch ($jobName) {
                quality { if ($mode -eq 'docs-only') { @('Checkout', 'Validate changed documentation') } else { @('Checkout', $confirmation) } }
                compile { @('Confirm addon checks unnecessary') }
                default { @($confirmation) }
            }
            Assert-Condition -Condition (($active -join '|') -ceq ($expected -join '|')) `
                -Message "Unexpected $mode steps in ${jobName}: $($active -join ', ')"
        }
        else {
            foreach ($step in $steps | Where-Object { $_.Name -notmatch '^(Confirm |Require |Validate changed documentation)' }) {
                Assert-Condition -Condition ($step.Name -cin $active) -Message "Full CI skipped $($step.Name)."
            }
        }
    }

    foreach ($status in @('failure', 'cancelled', 'skipped')) {
        $context['needs.map-menu-compile.result'] = $status
        $guard = @(Get-WorkflowSteps -Job $containerJob | Where-Object Name -eq 'Require successful addon compilation and packaging')[0]
        $blocks = Test-WorkflowCondition -Condition $guard.Condition -Context $context
        Assert-Condition -Condition ($blocks -eq ($mode -in @('addon-only', 'full'))) -Message "Addon $status propagation is incorrect for $mode."
        Assert-Condition -Condition ($guard.Body -match 'run: throw ') -Message "The addon result guard must fail the required check."
    }
    $context['needs.map-menu-compile.result'] = 'success'
    foreach ($dependency in @('needs.change-scope.result', 'needs.quality.result')) {
        foreach ($status in @('failure', 'cancelled', 'skipped')) {
            $context[$dependency] = $status
            $guard = @(Get-WorkflowSteps -Job $containerJob | Where-Object Name -eq 'Require successful prerequisite checks')[0]
            Assert-Condition -Condition (Test-WorkflowCondition -Condition $guard.Condition -Context $context) -Message "$dependency $status was ignored."
        }
        $context[$dependency] = 'success'
    }
}
Assert-Condition -Condition ($containerJob -match '(?m)^      - map-menu-compile$') -Message "Container Smoke must wait for addon compilation."
Assert-Condition -Condition ($containerJob -match '(?m)^    if: \$\{\{ !cancelled\(\) \}\}$') -Message "Container Smoke must report upstream failures."
Assert-Condition -Condition ($compileJob -notmatch '(?m)^    if:') -Message "Addon fast paths must report success rather than skip a publication ancestor."
Assert-Condition -Condition ($compileJob -match "'ubuntu-latest' \|\| 'windows-latest'") -Message "Addon compilation must use Windows; lightweight confirmations use Ubuntu."

try {
    New-Item -ItemType Directory -Path (Join-Path $temporaryDirectory "docs") | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $temporaryDirectory "docs/target.md"),
        "# Target`n")
    [IO.File]::WriteAllText(
        (Join-Path $temporaryDirectory "docs/good.md"),
        "# Good`n`n[Target](target.md#section)`n[External](https://example.com)`n")
    [IO.File]::WriteAllText(
        (Join-Path $temporaryDirectory "LICENSE"),
        "Test license`n")

    Push-Location -LiteralPath $temporaryDirectory
    try {
        $null = Invoke-TestGit -Arguments @("init", "--initial-branch=main", "--quiet")
        $null = Invoke-TestGit -Arguments @("config", "core.autocrlf", "false")
        $null = Invoke-TestGit -Arguments @("config", "commit.gpgsign", "false")
        $null = Invoke-TestGit -Arguments @("config", "user.email", "ci-fast-path@example.invalid")
        $null = Invoke-TestGit -Arguments @("config", "user.name", "CI Fast Path Smoke")
        $null = Invoke-TestGit -Arguments @("add", "--all")
        $null = Invoke-TestGit -Arguments @("commit", "--quiet", "-m", "baseline")
        $baseRevision = ([string](Invoke-TestGit -Arguments @("rev-parse", "HEAD"))).Trim()

        [IO.File]::AppendAllText(
            (Join-Path $temporaryDirectory "docs/good.md"),
            "`nDocumentation update.`n")
        $null = Invoke-TestGit -Arguments @("add", "--all")
        $null = Invoke-TestGit -Arguments @("commit", "--quiet", "-m", "docs")
        $docsRevision = ([string](Invoke-TestGit -Arguments @("rev-parse", "HEAD"))).Trim()
    }
    finally {
        Pop-Location
    }

    $githubOutputPath = Join-Path $temporaryDirectory "github-output.txt"
    $gitDocsResult = & $scopeScript `
        -EventName pull_request `
        -BaseRevision $baseRevision `
        -HeadRevision $docsRevision `
        -GitHubOutputPath $githubOutputPath `
        -RepositoryRoot $temporaryDirectory
    Assert-Condition -Condition $gitDocsResult.DocsOnly -Message "A documentation-only Git range selected the full CI path."

    $githubOutput = [IO.File]::ReadAllText($githubOutputPath)
    foreach ($expectedOutput in @(
            "docs_only=true",
            "stable_promotion=false",
            "addon_only=false",
            "mode=docs-only",
            "changed_count=1",
            "reason=documentation-only",
            "base_revision=$baseRevision",
            "head_revision=$docsRevision"
        )) {
        Assert-Condition `
            -Condition $githubOutput.Contains($expectedOutput, [StringComparison]::Ordinal) `
            -Message "The change-scope GitHub output is missing '$expectedOutput'."
    }

    $gitDocumentationResult = & $documentationScript `
        -RepositoryRoot $temporaryDirectory `
        -BaseRevision $baseRevision `
        -HeadRevision $docsRevision
    Assert-Condition `
        -Condition ($gitDocumentationResult.CheckedFiles -ge 2) `
        -Message "The Git-range documentation validator skipped tracked Markdown files."

    Remove-Item -LiteralPath (Join-Path $temporaryDirectory "docs/target.md")
    Assert-ThrowsLike `
        -ExpectedMessage "*link target does not exist*docs/good.md*target.md*" `
        -Action {
            $null = & $documentationScript `
                -RepositoryRoot $temporaryDirectory `
                -ChangedPath @("docs/target.md")
        }
    [IO.File]::WriteAllText(
        (Join-Path $temporaryDirectory "docs/target.md"),
        "# Target`n")

    New-Item -ItemType Directory -Path (Join-Path $temporaryDirectory "src") | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $temporaryDirectory "src/Program.cs"),
        "return;`n")
    Push-Location -LiteralPath $temporaryDirectory
    try {
        $null = Invoke-TestGit -Arguments @("add", "--all")
        $null = Invoke-TestGit -Arguments @("commit", "--quiet", "-m", "source")
        $sourceRevision = ([string](Invoke-TestGit -Arguments @("rev-parse", "HEAD"))).Trim()
    }
    finally {
        Pop-Location
    }

    $gitSourceResult = & $scopeScript `
        -EventName pull_request `
        -BaseRevision $docsRevision `
        -HeadRevision $sourceRevision `
        -RepositoryRoot $temporaryDirectory
    Assert-Condition -Condition (-not $gitSourceResult.DocsOnly) -Message "A source Git range selected the documentation-only CI path."

    New-Item -ItemType Directory -Path (Split-Path (Join-Path $temporaryDirectory $addonSource)) | Out-Null
    [IO.File]::WriteAllText((Join-Path $temporaryDirectory $addonSource), "// addon fixture`n")
    Push-Location -LiteralPath $temporaryDirectory
    try {
        $null = Invoke-TestGit -Arguments @("add", "--all")
        $null = Invoke-TestGit -Arguments @("commit", "--quiet", "-m", "addon")
        $addonRevision = ([string](Invoke-TestGit -Arguments @("rev-parse", "HEAD"))).Trim()
    }
    finally { Pop-Location }

    $addonOutputPath = Join-Path $temporaryDirectory "addon-output.txt"
    $gitAddonResult = & $scopeScript -EventName pull_request -BaseRevision $sourceRevision `
        -HeadRevision $addonRevision -RepositoryRoot $temporaryDirectory -GitHubOutputPath $addonOutputPath
    Assert-Condition -Condition ($gitAddonResult.Mode -eq 'addon-only') -Message "An addon Git range did not select addon-only."
    Assert-Condition -Condition ([IO.File]::ReadAllText($addonOutputPath).Contains("addon_only=true")) -Message "The addon decision was not exported."
    $gitMixedResult = & $scopeScript -EventName pull_request -BaseRevision $docsRevision `
        -HeadRevision $addonRevision -RepositoryRoot $temporaryDirectory
    Assert-Condition -Condition ($gitMixedResult.Mode -eq 'full') -Message "A mixed source/addon Git range did not select full CI."

    $mixedDocumentation = & $documentationScript -RepositoryRoot $temporaryDirectory `
        -ChangedPath @('docs/good.md', $addonSource) -AllowNonDocumentationChanges
    Assert-Condition -Condition ($mixedDocumentation.CheckedFiles -eq 2) -Message "Mixed documentation validation read an executable input or missed tracked Markdown."

    New-Item -ItemType Directory -Path (Split-Path (Join-Path $temporaryDirectory $mapSource)) | Out-Null
    Push-Location -LiteralPath $temporaryDirectory
    try {
        $null = Invoke-TestGit -Arguments @("mv", "src/Program.cs", $mapSource)
        $null = Invoke-TestGit -Arguments @("commit", "--quiet", "-m", "rename into addon")
        $renameRevision = ([string](Invoke-TestGit -Arguments @("rev-parse", "HEAD"))).Trim()
    }
    finally { Pop-Location }
    $gitRenameResult = & $scopeScript -EventName pull_request -BaseRevision $addonRevision `
        -HeadRevision $renameRevision -RepositoryRoot $temporaryDirectory
    Assert-Condition -Condition ($gitRenameResult.Mode -eq 'full') -Message "A rename hid a non-addon deletion."
    $gitMissingResult = & $scopeScript -EventName pull_request -BaseRevision ('f' * 40) `
        -HeadRevision $renameRevision -RepositoryRoot $temporaryDirectory -WarningAction SilentlyContinue
    Assert-Condition -Condition ($gitMissingResult.Mode -eq 'full') -Message "A missing Git object selected a reduced path."

    $result = & $documentationScript `
        -RepositoryRoot $temporaryDirectory `
        -ChangedPath @("docs/good.md", "LICENSE")
    Assert-Condition -Condition ($result.CheckedFiles -ge 2) -Message "The documentation validator skipped a changed file."
    Assert-Condition -Condition ($result.CheckedLocalLinks -eq 1) -Message "The documentation validator returned an unexpected local-link count."

    [IO.File]::WriteAllText(
        (Join-Path $temporaryDirectory "docs/missing.md"),
        "[Missing](does-not-exist.md)`n")
    Assert-ThrowsLike `
        -ExpectedMessage "*link target does not exist*docs/missing.md*does-not-exist.md*" `
        -Action {
            $null = & $documentationScript `
                -RepositoryRoot $temporaryDirectory `
                -ChangedPath @("docs/missing.md")
        }

    [IO.File]::WriteAllText(
        (Join-Path $temporaryDirectory "docs/no-newline.md"),
        "No final newline")
    Assert-ThrowsLike `
        -ExpectedMessage "*must end with a newline*docs/no-newline.md*" `
        -Action {
            $null = & $documentationScript `
                -RepositoryRoot $temporaryDirectory `
                -ChangedPath @("docs/no-newline.md")
        }

    Assert-ThrowsLike `
        -ExpectedMessage "*non-documentation path*src/Program.cs*" `
        -Action {
            $null = & $documentationScript `
                -RepositoryRoot $temporaryDirectory `
                -ChangedPath @("docs/good.md", "src/Program.cs")
        }
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        $resolvedTemporaryDirectory = (Resolve-Path -LiteralPath $temporaryDirectory).Path
        $expectedTemporaryDirectory = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-ci-fast-path-$runId"))
        if ($resolvedTemporaryDirectory -cne $expectedTemporaryDirectory) {
            throw "Refusing to remove an unexpected smoke directory."
        }
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
    }
}

Write-Host "CI release fast-path smoke passed."
