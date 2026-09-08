#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExporterAssemblyPath,

    [Parameter(Mandatory = $true)]
    [string]$WindowEndUtc,

    [switch]$PlanOnly,

    [string]$DotNetExecutable = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$windowEnd = [DateTimeOffset]::MinValue
$styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
    [Globalization.DateTimeStyles]::AdjustToUniversal
if (-not [DateTimeOffset]::TryParseExact(
        $WindowEndUtc,
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        [Globalization.CultureInfo]::InvariantCulture,
        $styles,
        [ref]$windowEnd) -or $windowEnd.Second -ne 0) {
    throw "WindowEndUtc must be an exact UTC minute: yyyy-MM-ddTHH:mm:00Z."
}

if ($windowEnd -gt [DateTimeOffset]::UtcNow.AddMinutes(-5)) {
    throw "WindowEndUtc must be at least five minutes old."
}

$windowStart = $windowEnd.AddHours(-24)
$plan = [pscustomobject]@{
    Status = if ($PlanOnly) { "Planned" } else { "Pending" }
    WindowStartUtc = $windowStart.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    WindowEndUtc = $windowEnd.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    ExpectedSlots = 1440
    EvaluatedSlots = 0
    PendingSlots = 0
    GoodSlots = 0
    BadSlots = 0
    MissingSlots = 0
    DuplicateRecords = 0
    IgnoredNonCanonicalAttempts = 0
    Availability = $null
    MeetsDraftTarget = $null
    Archived = $false
    RawEvidenceRetained = $false
}

if ($PlanOnly) {
    return $plan
}

foreach ($name in @(
        "GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE",
        "GOLDSRCOPS_GRAFANA_METRICS_URL",
        "GOLDSRCOPS_GRAFANA_METRICS_USER",
        "GOLDSRCOPS_GRAFANA_METRICS_TOKEN",
        "GOLDSRCOPS_GRAFANA_LOGS_URL",
        "GOLDSRCOPS_GRAFANA_LOGS_USER",
        "GOLDSRCOPS_GRAFANA_LOGS_TOKEN")) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Required environment variable '$name' is missing."
    }
}

$assembly = [IO.Path]::GetFullPath($ExporterAssemblyPath)
if (-not [IO.File]::Exists($assembly)) {
    throw "The exporter assembly does not exist."
}

$executable = (Get-Command -Name $DotNetExecutable -CommandType Application, ExternalScript |
    Select-Object -First 1).Source
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$directory = Join-Path $temporaryRoot "goldsrcops-shadow-audit-$([Guid]::NewGuid().ToString('N'))"
$segment = Join-Path $directory "primary.jsonl"
$reportPath = Join-Path $directory "report.json"
[IO.Directory]::CreateDirectory($directory) | Out-Null

function Get-ExporterFailureCategory {
    param([object[]]$Output)

    foreach ($line in $Output) {
        if ([string]$line -cmatch '^Operation failed: The (logs|metrics) API returned HTTP status ([1-5][0-9]{2})\.$') {
            return "$($Matches[1])_http_$($Matches[2])"
        }
        if ([string]$line -cmatch '^Operation failed: The (logs|metrics) API request timed out\.$') {
            return "$($Matches[1])_timeout"
        }
    }

    return "unclassified"
}

try {
    $exportOutput = @(& $executable $assembly export `
        --window-start $plan.WindowStartUtc --window-end $plan.WindowEndUtc `
        --job goldsrcops-api-ready-primary-shadow `
        --probe $env:GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE `
        --environment production --role primary `
        --monitor-revision v2-4-shadow-001 --location Frankfurt `
        --output $segment --overlap-minutes 10 --step-seconds 15 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $category = Get-ExporterFailureCategory -Output $exportOutput
        throw "Shadow export failed ($category); investigate the scoped read configuration."
    }

    if (-not [IO.File]::Exists($segment) -or
        ([IO.FileInfo]::new($segment)).Length -gt 16MB) {
        throw "Shadow export is missing or exceeds the evidence size limit."
    }

    $evaluatedAt = $windowEnd.AddMinutes(5).ToString(
        "O",
        [Globalization.CultureInfo]::InvariantCulture)
    $evaluationOutput = @(& $executable $assembly evaluate `
        --input $segment `
        --window-start $plan.WindowStartUtc --window-end $plan.WindowEndUtc `
        --evaluated-at $evaluatedAt `
        --monitor-revision v2-4-shadow-001 --location Frankfurt `
        --grace-minutes 5 --target 0.995 --output $reportPath 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Shadow evaluation failed; inspect the reviewed exporter revision."
    }

    if (-not [IO.File]::Exists($reportPath) -or
        ([IO.FileInfo]::new($reportPath)).Length -le 0 -or
        ([IO.FileInfo]::new($reportPath)).Length -gt 1MB) {
        throw "Shadow evaluation report is missing or exceeds the report size limit."
    }

    try {
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $reportStart = [DateTimeOffset]$report.window_start_utc
        $reportEnd = [DateTimeOffset]$report.window_end_utc
        if ($reportStart -ne $windowStart -or
            $reportEnd -ne $windowEnd -or
            $report.monitor_revision -cne "v2-4-shadow-001" -or
            $report.location -cne "Frankfurt" -or
            [int]$report.expected_slot_count -ne 1440 -or
            [int]$report.evaluated_slot_count -ne 1440 -or
            [int]$report.pending_slot_count -ne 0 -or
            [int]$report.missing_slot_count -ne 0 -or
            ([int]$report.good_slot_count + [int]$report.bad_slot_count) -ne 1440) {
            throw "The shadow report is incomplete or has an unexpected identity."
        }
    }
    catch {
        throw "Shadow audit failed: require exactly 1,440 mature slots with no missing data."
    }

    $plan.Status = "Passed"
    $plan.EvaluatedSlots = [int]$report.evaluated_slot_count
    $plan.PendingSlots = [int]$report.pending_slot_count
    $plan.GoodSlots = [int]$report.good_slot_count
    $plan.BadSlots = [int]$report.bad_slot_count
    $plan.MissingSlots = [int]$report.missing_slot_count
    $plan.DuplicateRecords = [int]$report.duplicate_record_count
    $plan.IgnoredNonCanonicalAttempts = [int]$report.ignored_non_canonical_attempt_count
    $plan.Availability = if ($null -eq $report.availability) {
        $null
    }
    else {
        [decimal]$report.availability
    }
    $plan.MeetsDraftTarget = if ($null -eq $report.meets_target) {
        $null
    }
    else {
        [bool]$report.meets_target
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        $availabilityText = if ($null -eq $plan.Availability) {
            "pending"
        }
        else {
            ([decimal]$plan.Availability).ToString("P5", [Globalization.CultureInfo]::InvariantCulture)
        }
        $targetText = switch ($plan.MeetsDraftTarget) {
            $true { "met" }
            $false { "missed" }
            default { "pending" }
        }

        @(
            "## Availability shadow audit",
            "",
            "- Evidence integrity: passed",
            "- Window start: ``$($plan.WindowStartUtc)``",
            "- Window end: ``$($plan.WindowEndUtc)``",
            "- Expected/evaluated/pending slots: ``1440/$($plan.EvaluatedSlots)/$($plan.PendingSlots)``",
            "- Good/bad/missing slots: ``$($plan.GoodSlots)/$($plan.BadSlots)/$($plan.MissingSlots)``",
            "- Duplicate records: ``$($plan.DuplicateRecords)``",
            "- Diagnostic non-canonical attempts: ``$($plan.IgnoredNonCanonicalAttempts)``",
            "- Shadow availability: ``$availabilityText``",
            "- Draft target at shadow scale: ``$targetText``",
            "- Archive writes: none",
            "- Raw evidence: deleted after evaluation",
            "- SLO status: not activated or achieved by this audit") |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }

    return $plan
}
finally {
    $resolvedDirectory = [IO.Path]::GetFullPath($directory)
    $prefix = $temporaryRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a shadow-audit directory outside the temporary root."
    }
    [IO.Directory]::Delete($resolvedDirectory, $true)
}
