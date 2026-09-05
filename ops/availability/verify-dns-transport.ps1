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

$windowStart = $windowEnd.AddMinutes(-15)
$plan = [pscustomobject]@{
    Status = if ($PlanOnly) { "Planned" } else { "Pending" }
    WindowStartUtc = $windowStart.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    WindowEndUtc = $windowEnd.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    Job = "goldsrcops-api-dns-validation"
    MonitorRevision = "v2-4-dns-validation-001"
    Role = "diagnostic"
    Environment = "validation"
    Location = "Frankfurt"
    DnsErrorSlots = 0
    Archived = $false
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
$directory = Join-Path $temporaryRoot "goldsrcops-dns-proof-$([Guid]::NewGuid().ToString('N'))"
$segment = Join-Path $directory "diagnostic.jsonl"
[IO.Directory]::CreateDirectory($directory) | Out-Null

try {
    # Child output is intentionally suppressed, including failure diagnostics.
    & $executable $assembly export `
        --window-start $plan.WindowStartUtc --window-end $plan.WindowEndUtc `
        --job $plan.Job --probe $env:GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE `
        --environment $plan.Environment --role $plan.Role `
        --monitor-revision $plan.MonitorRevision --location $plan.Location `
        --output $segment --overlap-minutes 0 --step-seconds 15 *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Diagnostic export failed; investigate the scoped read configuration."
    }
    if (-not [IO.File]::Exists($segment) -or
        ([IO.FileInfo]::new($segment)).Length -gt 131072) {
        throw "Diagnostic export is missing or exceeds the proof size limit."
    }

    try {
        $records = @(Get-Content -LiteralPath $segment | ConvertFrom-Json)
        $slots = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($record in $records) {
            if ($record.role -cne $plan.Role -or
                $record.monitor_revision -cne $plan.MonitorRevision -or
                $record.location -cne $plan.Location) {
                throw "Unexpected diagnostic identity."
            }

            $scheduled = [DateTimeOffset]$record.scheduled_at_utc
            if ($scheduled -lt $windowStart -or $scheduled -ge $windowEnd) {
                continue
            }
            if ($record.outcome -cne "dns_error" -or
                $null -ne $record.http_status -or
                $null -eq $record.started_at_utc -or
                $null -eq $record.completed_at_utc) {
                throw "Diagnostic classification or completion did not match."
            }
            $null = $slots.Add($scheduled.ToUniversalTime().ToString("yyyyMMddHHmm"))
        }
        if ($slots.Count -lt 3) {
            throw "Fewer than three distinct diagnostic slots."
        }
    }
    catch {
        throw "DNS proof failed: require at least three completed diagnostic slots, all dns_error with no HTTP status."
    }

    $plan.Status = "Passed"
    $plan.DnsErrorSlots = $slots.Count
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        @(
            "## Diagnostic DNS transport proof",
            "",
            "- Result: passed",
            "- Window start: ``$($plan.WindowStartUtc)``",
            "- Window end: ``$($plan.WindowEndUtc)``",
            "- Distinct DNS-error slots: ``$($slots.Count)``",
            "- Primary archive writes: none",
            "- Raw evidence: deleted after verification") |
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
        throw "Refusing to remove a diagnostic directory outside the temporary root."
    }
    [IO.Directory]::Delete($resolvedDirectory, $true)
}
