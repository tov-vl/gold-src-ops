#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExporterAssemblyPath,

    [Parameter(Mandatory = $true)]
    [string]$WorkingDirectory,

    [string]$WindowEndUtc = "",

    [ValidateRange(60, 1440)]
    [int]$LookbackMinutes = 360,

    [ValidateRange(5, 60)]
    [int]$IngestionDelayMinutes = 5,

    [switch]$PlanOnly,

    [string]$DotNetExecutable = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$requiredEnvironmentVariables = @(
    "GOLDSRCOPS_AVAILABILITY_PRIMARY_JOB",
    "GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE",
    "GOLDSRCOPS_AVAILABILITY_PRIMARY_LOCATION",
    "GOLDSRCOPS_AVAILABILITY_MONITOR_REVISION",
    "GOLDSRCOPS_GRAFANA_METRICS_URL",
    "GOLDSRCOPS_GRAFANA_METRICS_USER",
    "GOLDSRCOPS_GRAFANA_METRICS_TOKEN",
    "GOLDSRCOPS_B2_S3_ENDPOINT",
    "GOLDSRCOPS_B2_REGION",
    "GOLDSRCOPS_B2_BUCKET",
    "GOLDSRCOPS_B2_WRITE_KEY_ID",
    "GOLDSRCOPS_B2_WRITE_APPLICATION_KEY",
    "GOLDSRCOPS_B2_READ_KEY_ID",
    "GOLDSRCOPS_B2_READ_APPLICATION_KEY")

function Get-MinuteBoundary {
    param(
        [Parameter(Mandatory = $true)]
        [DateTimeOffset]$Value
    )

    $utc = $Value.ToUniversalTime()
    return [DateTimeOffset]::new(
        $utc.Year,
        $utc.Month,
        $utc.Day,
        $utc.Hour,
        $utc.Minute,
        0,
        [TimeSpan]::Zero)
}

function Resolve-WindowEnd {
    $latestEligible = (Get-MinuteBoundary -Value ([DateTimeOffset]::UtcNow)).AddMinutes(
        -$IngestionDelayMinutes)

    if ([string]::IsNullOrWhiteSpace($WindowEndUtc)) {
        return $latestEligible
    }

    $parsed = [DateTimeOffset]::MinValue
    $styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
        [Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [DateTimeOffset]::TryParseExact(
            $WindowEndUtc,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            [Globalization.CultureInfo]::InvariantCulture,
            $styles,
            [ref]$parsed)) {
        throw "WindowEndUtc must use the exact UTC-minute format yyyy-MM-ddTHH:mm:ssZ."
    }

    if ($parsed -gt $latestEligible) {
        throw "WindowEndUtc must not be newer than the ingestion-delay boundary."
    }

    return $parsed
}

function Get-RequiredEnvironmentValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required environment variable '$Name' is missing."
    }

    return $value
}

function Invoke-ExporterCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $script:resolvedDotNetPath $script:resolvedExporterPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Availability exporter command failed with exit code $LASTEXITCODE."
    }
}

$windowEnd = Resolve-WindowEnd
$windowStart = $windowEnd.AddMinutes(-$LookbackMinutes)
$plan = [pscustomobject]@{
    Action = "AvailabilityEvidenceExportCycle"
    Status = if ($PlanOnly) { "Planned" } else { "Pending" }
    WindowStartUtc = $windowStart.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    WindowEndUtc = $windowEnd.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    LookbackMinutes = $LookbackMinutes
    IngestionDelayMinutes = $IngestionDelayMinutes
}

if ($PlanOnly) {
    return $plan
}

foreach ($name in $requiredEnvironmentVariables) {
    $null = Get-RequiredEnvironmentValue -Name $name
}

$resolvedExporterPath = [IO.Path]::GetFullPath($ExporterAssemblyPath)
if (-not [IO.File]::Exists($resolvedExporterPath)) {
    throw "The availability exporter assembly does not exist."
}

$dotNetCommand = Get-Command `
    -Name $DotNetExecutable `
    -CommandType Application, ExternalScript `
    -ErrorAction Stop |
    Select-Object -First 1
$resolvedDotNetPath = $dotNetCommand.Source

$resolvedWorkingRoot = [IO.Path]::GetFullPath($WorkingDirectory)
[IO.Directory]::CreateDirectory($resolvedWorkingRoot) | Out-Null
$cycleDirectory = Join-Path `
    $resolvedWorkingRoot `
    "cycle-$($windowEnd.ToString('yyyyMMddTHHmmssZ', [Globalization.CultureInfo]::InvariantCulture))-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($cycleDirectory) | Out-Null
$segmentPath = Join-Path $cycleDirectory "primary.jsonl"

try {
    Invoke-ExporterCommand -Arguments @(
        "export",
        "--window-start", $plan.WindowStartUtc,
        "--window-end", $plan.WindowEndUtc,
        "--job", $env:GOLDSRCOPS_AVAILABILITY_PRIMARY_JOB,
        "--probe", $env:GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE,
        "--environment", "production",
        "--role", "primary",
        "--monitor-revision", $env:GOLDSRCOPS_AVAILABILITY_MONITOR_REVISION,
        "--location", $env:GOLDSRCOPS_AVAILABILITY_PRIMARY_LOCATION,
        "--output", $segmentPath,
        "--overlap-minutes", "10",
        "--step-seconds", "15")

    if (-not [IO.File]::Exists($segmentPath) -or
        ([IO.FileInfo]::new($segmentPath)).Length -le 0) {
        throw "The availability exporter did not create a non-empty segment."
    }

    Invoke-ExporterCommand -Arguments @(
        "archive",
        "--input", $segmentPath)

    $plan.Status = "Archived"

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        @(
            "## Availability evidence archive",
            "",
            "- Result: archived",
            "- Window start: ``$($plan.WindowStartUtc)``",
            "- Window end: ``$($plan.WindowEndUtc)``",
            "- Lookback: ``$LookbackMinutes`` minutes",
            "- Raw GitHub artifact: not retained") |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }

    return $plan
}
finally {
    if ([IO.Directory]::Exists($cycleDirectory)) {
        $resolvedCycleDirectory = [IO.Path]::GetFullPath($cycleDirectory)
        $expectedPrefix = $resolvedWorkingRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedCycleDirectory.StartsWith(
                $expectedPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a cycle directory outside the working root."
        }

        [IO.Directory]::Delete($resolvedCycleDirectory, $true)
    }
}
