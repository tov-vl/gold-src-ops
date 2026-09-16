#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$builder = Join-Path $repositoryRoot "tools/release/build-game-event-pilot-bundle.ps1"
$agentProject = Join-Path $repositoryRoot "src/GoldSrcOps.GameEventAgent/GoldSrcOps.GameEventAgent.csproj"
$smokeDirectory = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-game-event-pilot-$PID"
$testRevision = "1111111111111111111111111111111111111111"

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw $Message
}

function Expect-Failure {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    try {
        $outputPath = Join-Path $smokeDirectory "$Name.out"
        & $Action *> $outputPath
    }
    catch {
        Write-Output "Game-event pilot bundle case '$Name' failed as expected."
        return
    }

    Fail "Game-event pilot bundle case '$Name' passed unexpectedly."
}

try {
    New-Item -ItemType Directory -Force -Path $smokeDirectory | Out-Null

    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile(
        $builder,
        [ref]$tokens,
        [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -ne 0) {
        Fail "The game-event pilot bundle builder has PowerShell parse errors."
    }

    $planOutputDirectory = Join-Path $smokeDirectory "plan-output"
    $plan = & $builder `
        -Plan `
        -BundleVersion "2.11.0-pilot.1" `
        -SourceRevision $testRevision `
        -OutputDirectory $planOutputDirectory
    foreach ($expected in @(
        "self-contained linux-x64 single-file payload",
        "AMX Mod X 1.10.0.5481 and ReAPI 5.24.0.300",
        "ee33b31ae92afd94802c43eae14ecdfa1ffa2ba0b11658e8bec98f48a5881272",
        "76ff2bdd39f6dc14a2088ff4af724599c69a8ae7e3526679927aaef4ca898bf2",
        "Metamod-R 1.3.0.149",
        "ede7f59c4e0220afe8c02aa348a130cce527f87d36ffdb674e37a501ce57be94",
        "16114cf5a782e9d3d0c9443c23cd937c17cc09dc9b16ff998ce48bda5faf0a81",
        "all activation gates false",
        "PLAN_ONLY: no artifacts were downloaded, compiled, or written"
    )) {
        if (-not ($plan -match [regex]::Escape($expected))) {
            Fail "The game-event pilot bundle plan is missing '$expected'."
        }
    }
    if (Test-Path -LiteralPath $planOutputDirectory) {
        Fail "The game-event pilot bundle plan created an output directory."
    }
    if ($plan -match '(?i)password|bearer|applicationKey|client[_-]?secret\s*=') {
        Fail "The game-event pilot bundle plan exposed secret-shaped content."
    }

    Expect-Failure "invalid-version" {
        & $builder -Plan -BundleVersion "latest" -SourceRevision $testRevision
    }
    Expect-Failure "invalid-revision" {
        & $builder -Plan -BundleVersion "2.11.0-pilot.1" -SourceRevision "abc"
    }
    Expect-Failure "unsafe-replace" {
        & $builder `
            -Plan `
            -BundleVersion "2.11.0-pilot.1" `
            -SourceRevision $testRevision `
            -ReplaceDevelopmentOutput
    }

    [xml]$project = Get-Content -LiteralPath $agentProject -Raw
    $pilotPropertyGroup = @($project.Project.PropertyGroup |
        Where-Object { $_.GetAttribute("Condition") -eq "'`$(GameEventPilotBundle)' == 'true'" })
    if ($pilotPropertyGroup.Count -ne 1) {
        Fail "The agent project must contain exactly one pilot publish property group."
    }
    $pilotProperties = $pilotPropertyGroup[0]
    foreach ($property in @{
        RuntimeIdentifier = "linux-x64"
        SelfContained = "true"
        PublishSingleFile = "true"
        IncludeNativeLibrariesForSelfExtract = "true"
        PublishTrimmed = "false"
        DebugType = "embedded"
    }.GetEnumerator()) {
        if ($pilotProperties.($property.Key) -cne $property.Value) {
            Fail "The agent pilot publish property '$($property.Key)' is not pinned correctly."
        }
    }

    $builderSource = Get-Content -LiteralPath $builder -Raw
    foreach ($requiredSourceContract in @(
        "Get-VerifiedAsset",
        "Assert-SafeRelativePath",
        "`$productionEligible = -not `$DevelopmentBuild -and -not `$sourceDirty",
        "productionEligible = `$productionEligible",
        "changesGameServerRuntime = `$false",
        "producerEnabled = `$false",
        "spoolImportEnabled = `$false",
        "deliveryEnabled = `$false",
        'run|verify-access-token',
        'exec "$agent_directory/GoldSrcOps.GameEventAgent" "$agent_command"',
        "CreateFromDirectory",
        "Get-LowerSha256 `$bundlePath"
    )) {
        if (-not $builderSource.Contains($requiredSourceContract, [StringComparison]::Ordinal)) {
            Fail "The bundle builder is missing contract '$requiredSourceContract'."
        }
    }
    if ($builderSource -match '/releases/(latest|download/latest)|:[ \t]*latest') {
        Fail "The bundle builder contains a mutable latest reference."
    }

    Write-Output "Game-event pilot bundle contract smoke test passed."
}
finally {
    Remove-Item -LiteralPath $smokeDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
