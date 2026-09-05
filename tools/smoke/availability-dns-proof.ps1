#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$proof = Join-Path $PSScriptRoot "../../ops/availability/verify-dns-transport.ps1"
$root = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-dns-smoke-$([Guid]::NewGuid().ToString('N'))"
$fake = Join-Path $root "fake-exporter.ps1"
$log = Join-Path $root "output-path.txt"
$names = @(
    "GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE",
    "GOLDSRCOPS_GRAFANA_METRICS_URL", "GOLDSRCOPS_GRAFANA_METRICS_USER", "GOLDSRCOPS_GRAFANA_METRICS_TOKEN",
    "GOLDSRCOPS_GRAFANA_LOGS_URL", "GOLDSRCOPS_GRAFANA_LOGS_USER", "GOLDSRCOPS_GRAFANA_LOGS_TOKEN",
    "GOLDSRCOPS_FAKE_MODE", "GOLDSRCOPS_FAKE_PATH_LOG", "GITHUB_STEP_SUMMARY")
$previous = @{}
foreach ($name in $names) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Assert-Fails {
    param([scriptblock]$Operation, [string]$Message)
    try {
        & $Operation *> $null
    }
    catch {
        if ($_.Exception.Message -notlike $Message) { throw }
        return
    }
    throw "Expected failure: $Message"
}

function Assert-Cleaned {
    if (Test-Path -LiteralPath $log) {
        $output = [IO.File]::ReadAllText($log)
        if (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($output))) {
            throw "Diagnostic evidence survived cleanup."
        }
    }
}

try {
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $source = @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CommandArguments)
$ErrorActionPreference = "Stop"
if ($CommandArguments[0] -cne "export" -or $CommandArguments -contains "archive") {
    throw "Only export is permitted."
}
foreach ($name in @("GOLDSRCOPS_GRAFANA_LOGS_URL", "GOLDSRCOPS_GRAFANA_LOGS_USER", "GOLDSRCOPS_GRAFANA_LOGS_TOKEN")) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value) -or $CommandArguments -contains $value) {
        throw "Credential boundary violated."
    }
}
$outputPath = $CommandArguments[[Array]::IndexOf($CommandArguments, "--output") + 1]
[IO.File]::WriteAllText($env:GOLDSRCOPS_FAKE_PATH_LOG, $outputPath)
Write-Output "private-child-output"
if ($env:GOLDSRCOPS_FAKE_MODE -eq "http-error") {
    [Console]::Error.WriteLine("Operation failed: The logs API returned HTTP status 400.")
    exit 1
}
if ($env:GOLDSRCOPS_FAKE_MODE -eq "export-fail") { exit 1 }
if ($env:GOLDSRCOPS_FAKE_MODE -eq "empty") { [IO.File]::WriteAllText($outputPath, ""); exit 0 }
if ($env:GOLDSRCOPS_FAKE_MODE -eq "malformed") { [IO.File]::WriteAllText($outputPath, "{private-payload"); exit 0 }
if ($env:GOLDSRCOPS_FAKE_MODE -eq "oversize") { [IO.File]::WriteAllText($outputPath, ('x' * 131073)); exit 0 }
$lines = foreach ($minute in 1..3) {
    $record = @{
        scheduled_at_utc = "2026-09-04T09:5${minute}:00Z"
        started_at_utc = "2026-09-04T09:5${minute}:01Z"
        completed_at_utc = "2026-09-04T09:5${minute}:02Z"
        role = "diagnostic"; monitor_revision = "v2-4-dns-validation-001"; location = "Frankfurt"
        outcome = "dns_error"; http_status = $null
    }
    switch ($env:GOLDSRCOPS_FAKE_MODE) {
        "duplicate" { $record.scheduled_at_utc = "2026-09-04T09:51:00Z" }
        "outside" { $record.scheduled_at_utc = "2026-09-04T09:00:00Z" }
        "primary" { $record.role = "primary" }
        "revision" { $record.monitor_revision = "other" }
        "location" { $record.location = "other" }
        "unknown" { $record.outcome = "monitor_error" }
        "http" { $record.http_status = 500 }
        "incomplete" { $record.completed_at_utc = $null }
    }
    $record | ConvertTo-Json -Compress
}
[IO.File]::WriteAllLines($outputPath, $lines)
'@
    [IO.File]::WriteAllText($fake, $source)
    foreach ($name in $names) {
        [Environment]::SetEnvironmentVariable($name, "smoke-$name")
    }
    $env:GITHUB_STEP_SUMMARY = Join-Path $root "summary.md"
    $env:GOLDSRCOPS_FAKE_PATH_LOG = $log
    $env:GOLDSRCOPS_FAKE_MODE = "success"
    $arguments = @{
        ExporterAssemblyPath = $fake
        WindowEndUtc = "2026-09-04T10:00:00Z"
        DotNetExecutable = (Get-Process -Id $PID).Path
    }
    $plan = & $proof @arguments -PlanOnly
    if ($plan.Status -cne "Planned" -or $plan.Archived -or (Test-Path -LiteralPath $log)) {
        throw "Plan-only mode must not invoke the exporter."
    }
    foreach ($invalid in @("2026-09-04T10:00:01Z", "2026-09-04T10:00:00+00:00")) {
        Assert-Fails { & $proof -ExporterAssemblyPath $fake -WindowEndUtc $invalid -PlanOnly } "*exact UTC minute*"
    }
    Assert-Fails { & $proof -ExporterAssemblyPath $fake -WindowEndUtc "2099-01-01T00:00:00Z" -PlanOnly } "*five minutes old*"
    $result = @(& $proof @arguments)
    if ($result.Count -ne 1 -or $result[0].Status -cne "Passed" -or
        $result[0].DnsErrorSlots -ne 3 -or $result[0].Archived) {
        throw "Successful proof did not return exactly one sanitized result."
    }
    Assert-Cleaned
    foreach ($mode in @("duplicate", "outside", "primary", "revision", "location", "unknown", "http", "incomplete", "empty", "malformed")) {
        $env:GOLDSRCOPS_FAKE_MODE = $mode
        Assert-Fails { & $proof @arguments } "DNS proof failed:*"
        Assert-Cleaned
    }
    $env:GOLDSRCOPS_FAKE_MODE = "oversize"
    Assert-Fails { & $proof @arguments } "*proof size limit*"
    Assert-Cleaned
    $env:GOLDSRCOPS_FAKE_MODE = "export-fail"
    Assert-Fails { & $proof @arguments } "Diagnostic export failed*"
    Assert-Cleaned
    $env:GOLDSRCOPS_FAKE_MODE = "http-error"
    Assert-Fails { & $proof @arguments } "Diagnostic export failed (logs_http_400)*"
    Assert-Cleaned
    $env:GOLDSRCOPS_GRAFANA_LOGS_TOKEN = ""
    Assert-Fails { & $proof @arguments } "*GOLDSRCOPS_GRAFANA_LOGS_TOKEN*missing*"

    $workflow = Get-Content (Join-Path $PSScriptRoot "../../.github/workflows/availability-dns-proof.yml") -Raw
    foreach ($required in @("workflow_dispatch:", "github.ref == 'refs/heads/main'", 'ref: ${{ github.sha }}',
            "contents: read", "persist-credentials: false", "deployment: false", "timeout-minutes: 10",
            "verify-dns-transport.ps1", 'WindowEndUtc $env:REQUESTED_WINDOW_END_UTC')) {
        if (-not $workflow.Contains($required)) { throw "Missing workflow safeguard: $required" }
    }
    foreach ($forbidden in @("GOLDSRCOPS_B2", "upload-artifact", 'schedule:')) {
        if ($workflow.Contains($forbidden)) { throw "Unexpected proof workflow surface: $forbidden" }
    }
    $verificationStep = $workflow.IndexOf("      - name: Verify diagnostic evidence without archiving")
    if ($verificationStep -lt 0 -or $workflow.Substring(0, $verificationStep).Contains("secrets.")) {
        throw "Secrets must only reach the verification step."
    }
    $runBody = $workflow.Substring($workflow.LastIndexOf("        run: |"))
    if ($runBody.Contains('${{ inputs.') -or $runBody.Contains('${{ secrets.')) {
        throw "Inputs and secrets must not be interpolated into executable workflow code."
    }
    foreach ($name in $names | Where-Object { $_ -like "GOLDSRCOPS_GRAFANA*" }) {
        $mapping = ('{0}: ${{{{ secrets.{0} }}}}' -f $name)
        if (-not $workflow.Contains($mapping)) { throw "Missing environment mapping: $name" }
    }
    Write-Host "Diagnostic DNS proof smoke passed: scoped workflow, validation, distinct slots, failures, sanitization, and cleanup."
}
finally {
    foreach ($name in $names) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name])
    }
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a smoke directory outside the temporary root."
    }
    if ([IO.Directory]::Exists($resolved)) { [IO.Directory]::Delete($resolved, $true) }
}
