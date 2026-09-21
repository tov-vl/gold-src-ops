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

        [bool]$ExpectedStablePromotion = $false
    )

    $result = & $scopeScript -EventName pull_request -ChangedPath $Paths
    Assert-Condition `
        -Condition ($result.DocsOnly -eq $ExpectedDocsOnly) `
        -Message "Change-scope case '$Name' returned '$($result.Mode)'."
    Assert-Condition `
        -Condition ($result.StablePromotion -eq $ExpectedStablePromotion) `
        -Message "Change-scope case '$Name' returned unexpected stable-promotion state '$($result.StablePromotion)'."
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

$emptyResult = & $scopeScript -EventName pull_request -ChangedPath @()
Assert-Condition -Condition (-not $emptyResult.DocsOnly) -Message "An empty diff was accepted as documentation-only."

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
Assert-Condition `
    -Condition ($scopeJob -match '(?m)^      stable_promotion: \$\{\{ steps\.scope\.outputs\.stable_promotion \}\}$') `
    -Message "Change Scope does not expose the stable-promotion decision."
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
        -Condition ($job -match '(?m)^      - name: Checkout\r?\n        if: needs\.change-scope\.outputs\.docs_only != ''true'' && needs\.change-scope\.outputs\.stable_promotion != ''true''\r?\n        uses: actions/checkout@v6') `
        -Message "Runtime jobs must skip checkout only on an explicit documentation or stable-promotion fast path."
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
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

Write-Host "CI release fast-path smoke passed."
