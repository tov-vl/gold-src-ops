#Requires -Version 7.0

<#
.SYNOPSIS
Exercises the v2.3 soak-readiness evaluator with deterministic snapshots.

.DESCRIPTION
Validates passing, in-progress, and focused failure paths without accessing a
target host. Snapshot evidence is always non-target evidence, stays outside the
repository, and is removed after the smoke run.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$readinessScript = Join-Path $repoRoot "ops/production/soak-readiness.ps1"
$runId = [Guid]::NewGuid().ToString("N")
$smokeDirectory = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-soak-readiness-$runId"
$forbiddenEvidenceFile = Join-Path $repoRoot ".soak-readiness-$runId.json"
$startedAtUtc = [DateTimeOffset]::new(
    2026,
    9,
    1,
    0,
    0,
    0,
    [TimeSpan]::Zero)
$requiredServices = @(
    "api",
    "caddy",
    "grafana",
    "otel-collector",
    "postgres",
    "prometheus"
)

function New-Baseline {
    $services = foreach ($service in $requiredServices) {
        [ordered]@{
            Service = $service
            Image = "registry.example/$service@sha256:$('a' * 64)"
            StartedAtUtc = $startedAtUtc.AddHours(-1)
            RestartCount = 0
        }
    }

    return [ordered]@{
        SchemaVersion = 1
        Action = "V23SoakBaseline"
        StartedAtUtc = $startedAtUtc
        ExpectedCompletionAtUtc = $startedAtUtc.AddHours(24)
        RequiredDurationHours = 24
        Candidate = [ordered]@{
            Version = "2.3.0-rc.5"
            SourceRevision = "0123456789abcdef0123456789abcdef01234567"
            ApiImageDigest = "sha256:$('b' * 64)"
            MigrationRevision = "20260901000000_Ready"
        }
        ControlPlane = [ordered]@{
            Services = @($services)
        }
    }
}

function New-ReadySnapshot {
    param(
        [ValidateRange(1, 48)]
        [double]$ElapsedHours = 24
    )

    $collectedAtUtc = $startedAtUtc.AddHours($ElapsedHours)
    $pollCount = [int][Math]::Floor($ElapsedHours * 60)
    $scrapeCount = [int][Math]::Floor($ElapsedHours * 60 * 4)
    $services = foreach ($service in $requiredServices) {
        [ordered]@{
            Service = $service
            Running = $true
            Image = "registry.example/$service@sha256:$('a' * 64)"
            StartedAtUtc = $startedAtUtc.AddHours(-1)
            RestartCount = 0
            Health = "healthy"
        }
    }

    return [ordered]@{
        SchemaVersion = 1
        CollectedAtUtc = $collectedAtUtc
        Candidate = [ordered]@{
            Version = "2.3.0-rc.5"
            SourceRevision = "0123456789abcdef0123456789abcdef01234567"
            ApiImageDigest = "sha256:$('b' * 64)"
            MigrationRevision = "20260901000000_Ready"
        }
        Services = @($services)
        Edge = [ordered]@{
            LivenessHttpStatus = 200
            ReadinessHttpStatus = 200
            AlertDeliveryEnabled = $false
        }
        Backup = [ordered]@{
            Fresh = $true
        }
        Database = [ordered]@{
            serverCount = 1
            enabledServerCount = 1
            onlineServerCount = 1
            pollIntervalSeconds = 60
            latestPollAtUtc = $collectedAtUtc.AddSeconds(-30)
            pollTotal = $pollCount
            pollSuccessful = $pollCount
            pollFailed = 0
            botPositivePolls = 0
            latencySampleCount = $pollCount
            averageLatencyMs = 12.25
            p95LatencyMs = 19.5
            maximumLatencyMs = 25
            maximumPollGapSeconds = 61
            incidentsOpened = 0
            openIncidentCount = 0
            maximumIncidentDurationSeconds = $null
            commandsTotal = 0
            commandsSucceeded = 0
            commandsFailed = 0
            incompleteCommandCount = 0
            outboxEventsTotal = 0
            outboxEventsProcessed = 0
            pendingOutboxCount = 0
            deadLetterCount = 0
            databaseSizeBytes = 64MB
            pollSnapshotsTableSizeBytes = 16MB
        }
        Telemetry = [ordered]@{
            ScrapeIntervalSeconds = 15
            GoldSrcOpsUp = 1
            GoldSrcOpsSamples = $scrapeCount
            GoldSrcOpsHealthySamples = $scrapeCount
            CollectorUp = 1
            CollectorSamples = $scrapeCount
            CollectorHealthySamples = $scrapeCount
        }
        Host = [ordered]@{
            FreeDiskBytes = 30GB
            AvailableMemoryBytes = 2GB
            ProcessorCount = 2
            LoadAverageOneMinute = 0.25
        }
    }
}

function Invoke-SoakCase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$Snapshot,

        [Parameter(Mandatory = $true)]
        [bool]$ShouldPass,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Passed", "Failed", "InProgress")]
        [string]$ExpectedResult,

        [switch]$AllowIncomplete
    )

    $safeName = $Name.ToLowerInvariant() -replace '[^a-z0-9]+', '-'
    $baselineFile = Join-Path $smokeDirectory "$safeName-baseline.json"
    $snapshotFile = Join-Path $smokeDirectory "$safeName-snapshot.json"
    $evidenceFile = Join-Path $smokeDirectory "$safeName-evidence.json"
    New-Baseline | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $baselineFile -Encoding utf8NoBOM
    $Snapshot | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $snapshotFile -Encoding utf8NoBOM

    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @(
            "-NoLogo",
            "-NoProfile",
            "-File",
            $readinessScript,
            "-BaselineFile",
            $baselineFile,
            "-SnapshotFile",
            $snapshotFile,
            "-EvidenceFile",
            $evidenceFile
        )) {
        $arguments.Add([string]$argument)
    }
    if ($AllowIncomplete) {
        $arguments.Add("-AllowIncomplete")
    }

    $output = @(& pwsh @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($ShouldPass -and $exitCode -ne 0) {
        throw "Soak-readiness case '$Name' failed unexpectedly: $($output -join [Environment]::NewLine)"
    }
    if (-not $ShouldPass -and $exitCode -eq 0) {
        throw "Soak-readiness case '$Name' passed unexpectedly."
    }
    if (-not (Test-Path -LiteralPath $evidenceFile -PathType Leaf)) {
        throw "Soak-readiness case '$Name' did not write evidence."
    }

    $evidenceText = Get-Content -LiteralPath $evidenceFile -Raw
    $evidence = $evidenceText | ConvertFrom-Json -Depth 100
    if ([int]$evidence.SchemaVersion -ne 1 -or $evidence.Action -ne "V23SoakReadiness") {
        throw "Soak-readiness case '$Name' wrote an unexpected evidence contract."
    }
    if ($evidence.Source -ne "Snapshot" -or [bool]$evidence.TargetEvidence) {
        throw "Snapshot evidence could be mistaken for live target evidence."
    }
    if ($evidence.Result -ne $ExpectedResult) {
        throw "Soak-readiness case '$Name' recorded '$($evidence.Result)' instead of '$ExpectedResult'."
    }
    if ([int]$evidence.Indicators.Polling.LatencySampleCount -ne
        [int]$Snapshot.Database.latencySampleCount) {
        throw "Soak-readiness case '$Name' did not preserve the latency sample count."
    }
    if ($evidenceText -match '(?i)password|authorization|203\.0\.113\.') {
        throw "Soak-readiness evidence contains a forbidden sensitive marker."
    }

    if ($IsLinux) {
        $mode = [IO.File]::GetUnixFileMode($evidenceFile)
        $expectedMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
        if ($mode -ne $expectedMode) {
            throw "Soak-readiness evidence mode is '$mode' instead of owner-only read/write."
        }
    }

    Write-Host "Soak-readiness case '$Name' behaved as expected."
}

function Assert-RepositoryEvidenceRejected {
    $baselineFile = Join-Path $smokeDirectory "forbidden-baseline.json"
    $snapshotFile = Join-Path $smokeDirectory "forbidden-snapshot.json"
    New-Baseline | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $baselineFile -Encoding utf8NoBOM
    New-ReadySnapshot | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $snapshotFile -Encoding utf8NoBOM

    $output = @(& pwsh -NoLogo -NoProfile -File $readinessScript `
            -BaselineFile $baselineFile `
            -SnapshotFile $snapshotFile `
            -EvidenceFile $forbiddenEvidenceFile 2>&1)
    if ($LASTEXITCODE -eq 0) {
        throw "Repository-local soak evidence was accepted unexpectedly."
    }
    if (Test-Path -LiteralPath $forbiddenEvidenceFile) {
        throw "Repository-local soak evidence was written unexpectedly."
    }
    if (($output -join [Environment]::NewLine) -notmatch 'outside the repository') {
        throw "Repository-local evidence rejection did not explain the boundary."
    }

    Write-Host "Repository-local soak evidence was rejected as expected."
}

try {
    [void](New-Item -ItemType Directory -Path $smokeDirectory)

    Invoke-SoakCase `
        -Name "completed" `
        -Snapshot (New-ReadySnapshot) `
        -ShouldPass $true `
        -ExpectedResult "Passed"

    Invoke-SoakCase `
        -Name "in progress allowed" `
        -Snapshot (New-ReadySnapshot -ElapsedHours 12) `
        -ShouldPass $true `
        -ExpectedResult "InProgress" `
        -AllowIncomplete

    Invoke-SoakCase `
        -Name "in progress rejected" `
        -Snapshot (New-ReadySnapshot -ElapsedHours 12) `
        -ShouldPass $false `
        -ExpectedResult "Failed"

    $candidateDrift = New-ReadySnapshot
    $candidateDrift.Candidate["Version"] = "2.3.0-rc.6"
    Invoke-SoakCase `
        -Name "candidate drift" `
        -Snapshot $candidateDrift `
        -ShouldPass $false `
        -ExpectedResult "Failed"

    $runtimeRestart = New-ReadySnapshot
    $runtimeRestart.Services[0]["RestartCount"] = 1
    Invoke-SoakCase `
        -Name "runtime restart" `
        -Snapshot $runtimeRestart `
        -ShouldPass $false `
        -ExpectedResult "Failed"

    $pollingConfigurationDrift = New-ReadySnapshot
    $pollingConfigurationDrift.Database["pollIntervalSeconds"] = 120
    Invoke-SoakCase `
        -Name "polling configuration drift" `
        -Snapshot $pollingConfigurationDrift `
        -ShouldPass $false `
        -ExpectedResult "Failed"

    $pollFailure = New-ReadySnapshot
    $pollFailure.Database["pollSuccessful"]--
    $pollFailure.Database["pollFailed"] = 1
    $pollFailure.Database["botPositivePolls"] = 1
    Invoke-SoakCase `
        -Name "poll failure" `
        -Snapshot $pollFailure `
        -ShouldPass $false `
        -ExpectedResult "Failed"

    $latencySampleGap = New-ReadySnapshot
    $latencySampleGap.Database["latencySampleCount"]--
    Invoke-SoakCase `
        -Name "latency sample gap" `
        -Snapshot $latencySampleGap `
        -ShouldPass $false `
        -ExpectedResult "Failed"

    $telemetryGap = New-ReadySnapshot
    $telemetryGap.Telemetry["GoldSrcOpsSamples"] = 100
    $telemetryGap.Telemetry["GoldSrcOpsHealthySamples"] = 100
    Invoke-SoakCase `
        -Name "telemetry gap" `
        -Snapshot $telemetryGap `
        -ShouldPass $false `
        -ExpectedResult "Failed"

    Assert-RepositoryEvidenceRejected

    Write-Host "Soak-readiness smoke passed."
}
finally {
    if (Test-Path -LiteralPath $forbiddenEvidenceFile) {
        Remove-Item -LiteralPath $forbiddenEvidenceFile -Force
    }
    if (Test-Path -LiteralPath $smokeDirectory) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedSmokeDirectory = [IO.Path]::GetFullPath($smokeDirectory)
        if (-not $resolvedSmokeDirectory.StartsWith(
                $temporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a soak-readiness directory outside the system temporary path."
        }

        Remove-Item -LiteralPath $resolvedSmokeDirectory -Recurse -Force
    }
}
