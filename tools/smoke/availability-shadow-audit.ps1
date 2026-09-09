#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$auditPath = Join-Path $PSScriptRoot "../../ops/availability/run-shadow-audit.ps1"
$workflowPath = Join-Path $PSScriptRoot "../../.github/workflows/availability-shadow-audit.yml"
$root = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "goldsrcops-shadow-audit-smoke-$([Guid]::NewGuid().ToString('N'))"
$fakeExporter = Join-Path $root "fake-exporter.ps1"
$invocationLog = Join-Path $root "invocations.jsonl"
$summaryPath = Join-Path $root "summary.md"
$environmentNames = @(
    "GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE",
    "GOLDSRCOPS_GRAFANA_METRICS_URL",
    "GOLDSRCOPS_GRAFANA_METRICS_USER",
    "GOLDSRCOPS_GRAFANA_METRICS_TOKEN",
    "GOLDSRCOPS_GRAFANA_LOGS_URL",
    "GOLDSRCOPS_GRAFANA_LOGS_USER",
    "GOLDSRCOPS_GRAFANA_LOGS_TOKEN",
    "GOLDSRCOPS_FAKE_AUDIT_MODE",
    "GOLDSRCOPS_FAKE_AUDIT_LOG",
    "GITHUB_STEP_SUMMARY")
$previousEnvironment = @{}

function Assert-Fails {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Operation,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessage
    )

    try {
        & $Operation *> $null
    }
    catch {
        if ($_.Exception.Message -notlike $ExpectedMessage) {
            throw
        }
        return
    }

    throw "Expected operation to fail with '$ExpectedMessage'."
}

function Get-ArgumentValue {
    param([object[]]$Arguments, [string]$Name)

    $index = [Array]::IndexOf($Arguments, $Name)
    if ($index -lt 0 -or $index + 1 -ge $Arguments.Count) {
        throw "Missing fake exporter argument '$Name'."
    }
    return [string]$Arguments[$index + 1]
}

function Assert-AuditDirectoriesCleaned {
    if (-not [IO.File]::Exists($invocationLog)) {
        return
    }

    foreach ($line in [IO.File]::ReadAllLines($invocationLog)) {
        $arguments = @($line | ConvertFrom-Json)
        if ($arguments -notcontains "--output") {
            continue
        }
        $path = Get-ArgumentValue -Arguments $arguments -Name "--output"
        if ([IO.Directory]::Exists([IO.Path]::GetDirectoryName($path))) {
            throw "Shadow audit retained a temporary evidence directory."
        }
    }
}

foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

try {
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $fakeSource = @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CommandArguments)
$ErrorActionPreference = "Stop"
[IO.File]::AppendAllText(
    $env:GOLDSRCOPS_FAKE_AUDIT_LOG,
    (($CommandArguments | ConvertTo-Json -Compress -AsArray) + [Environment]::NewLine))
foreach ($name in @(
        "GOLDSRCOPS_GRAFANA_METRICS_TOKEN",
        "GOLDSRCOPS_GRAFANA_LOGS_TOKEN")) {
    if ($CommandArguments -contains [Environment]::GetEnvironmentVariable($name)) {
        throw "A credential crossed the child argument boundary."
    }
}
function Get-Value([string]$Name) {
    $index = [Array]::IndexOf($CommandArguments, $Name)
    if ($index -lt 0) { throw "Missing argument $Name." }
    return $CommandArguments[$index + 1]
}
$command = $CommandArguments[0]
if ($command -eq "export") {
    if ($CommandArguments -contains "archive") { throw "Archive is forbidden." }
    if ($env:GOLDSRCOPS_FAKE_AUDIT_MODE -eq "export-fail") {
        [Console]::Error.WriteLine("Operation failed: The metrics API returned HTTP status 503.")
        exit 1
    }
    $output = Get-Value "--output"
    if ($env:GOLDSRCOPS_FAKE_AUDIT_MODE -eq "oversize") {
        [IO.File]::WriteAllText($output, ('x' * (16MB + 1)))
    }
    else {
        [IO.File]::WriteAllText($output, "{}" + [Environment]::NewLine)
    }
    exit 0
}
if ($command -eq "evaluate") {
    if ($env:GOLDSRCOPS_FAKE_AUDIT_MODE -eq "evaluate-fail") { exit 1 }
    $expected = 1440
    $evaluated = 1440
    $pending = 0
    $good = 1440
    $bad = 0
    $missing = 0
    if ($env:GOLDSRCOPS_FAKE_AUDIT_MODE -eq "wrong-slots") { $expected = 1439 }
    if ($env:GOLDSRCOPS_FAKE_AUDIT_MODE -eq "pending") {
        $evaluated = 1439; $pending = 1; $good = 1439
    }
    if ($env:GOLDSRCOPS_FAKE_AUDIT_MODE -eq "missing") {
        $good = 1439; $bad = 1; $missing = 1
    }
    if ($env:GOLDSRCOPS_FAKE_AUDIT_MODE -eq "target-miss") {
        $good = 1432; $bad = 8; $missing = 8
    }
    if ($env:GOLDSRCOPS_FAKE_AUDIT_MODE -eq "malformed-counts") {
        $missing = 1
    }
    $report = [ordered]@{
        evaluator_revision = "availability-evaluator-v1"
        window_start_utc = Get-Value "--window-start"
        window_end_utc = Get-Value "--window-end"
        evaluated_at_utc = Get-Value "--evaluated-at"
        monitor_revision = Get-Value "--monitor-revision"
        location = Get-Value "--location"
        expected_slot_count = $expected
        evaluated_slot_count = $evaluated
        pending_slot_count = $pending
        good_slot_count = $good
        bad_slot_count = $bad
        missing_slot_count = $missing
        duplicate_record_count = 0
        ignored_non_canonical_attempt_count = 0
        availability = if ($good -eq 1440) { 1.0 } else { $good / 1440.0 }
        target_availability = 0.995
        allowed_bad_slot_count = 7
        meets_target = if ($pending -eq 0) { $bad -le 7 } else { $null }
        outcomes = @{ good = $good; missing = $missing }
    }
    $report | ConvertTo-Json -Compress | Set-Content -LiteralPath (Get-Value "--output")
    exit 0
}
throw "Unexpected command '$command'."
'@
    [IO.File]::WriteAllText($fakeExporter, $fakeSource)

    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, "smoke-$name")
    }
    $env:GOLDSRCOPS_FAKE_AUDIT_LOG = $invocationLog
    $env:GITHUB_STEP_SUMMARY = $summaryPath
    $env:GOLDSRCOPS_FAKE_AUDIT_MODE = "success"
    $arguments = @{
        ExporterAssemblyPath = $fakeExporter
        WindowEndUtc = "2026-09-04T10:00:00Z"
        DotNetExecutable = (Get-Process -Id $PID).Path
    }

    $plan = & $auditPath @arguments -PlanOnly
    if ($plan.Status -cne "Planned" -or $plan.ExpectedSlots -ne 1440 -or
        [IO.File]::Exists($invocationLog)) {
        throw "Plan-only mode must describe one day without invoking the exporter."
    }

    foreach ($invalid in @("2026-09-04T10:00:01Z", "2026-09-04T10:00:00+00:00")) {
        Assert-Fails `
            -Operation { & $auditPath @arguments -WindowEndUtc $invalid -PlanOnly } `
            -ExpectedMessage "*exact UTC minute*"
    }
    Assert-Fails `
        -Operation { & $auditPath @arguments -WindowEndUtc "2099-01-01T00:00:00Z" -PlanOnly } `
        -ExpectedMessage "*five minutes old*"

    $result = @(& $auditPath @arguments)
    if ($result.Count -ne 1 -or $result[0].Status -cne "Passed" -or
        $result[0].ExpectedSlots -ne 1440 -or $result[0].EvaluatedSlots -ne 1440 -or
        $result[0].PendingSlots -ne 0 -or $result[0].MissingSlots -ne 0 -or
        -not $result[0].IdentityMatched -or -not $result[0].PopulationComplete -or
        $result[0].Archived -or $result[0].RawEvidenceRetained) {
        throw "Successful shadow audit did not return the expected sanitized result."
    }
    Assert-AuditDirectoriesCleaned

    $invocations = [Collections.Generic.List[object[]]]::new()
    foreach ($line in [IO.File]::ReadAllLines($invocationLog)) {
        $invocations.Add(@($line | ConvertFrom-Json))
    }
    $containsArchive = $false
    foreach ($invocation in $invocations) {
        $containsArchive = $containsArchive -or $invocation -contains "archive"
    }
    if ($invocations.Count -ne 2 -or $invocations[0][0] -cne "export" -or
        $invocations[1][0] -cne "evaluate" -or $containsArchive) {
        throw "Shadow audit must invoke only export followed by evaluate."
    }

    $summary = [IO.File]::ReadAllText($summaryPath)
    foreach ($required in @("Shadow result: passed", "Evidence integrity: passed", "1440/1440/0", "Archive writes: none",
            "Raw evidence: deleted after evaluation", "not activated or achieved")) {
        if (-not $summary.Contains($required, [StringComparison]::Ordinal)) {
            throw "Shadow audit summary is missing '$required'."
        }
    }
    foreach ($secretName in $environmentNames | Where-Object { $_ -like "*TOKEN" }) {
        if ($summary.Contains([Environment]::GetEnvironmentVariable($secretName), [StringComparison]::Ordinal)) {
            throw "Shadow audit summary exposed a credential value."
        }
    }

    Remove-Item -LiteralPath $summaryPath -Force -ErrorAction SilentlyContinue
    $env:GOLDSRCOPS_FAKE_AUDIT_MODE = "missing"
    $missingResult = @(& $auditPath @arguments)
    if ($missingResult.Count -ne 1 -or $missingResult[0].Status -cne "Passed" -or
        $missingResult[0].MissingSlots -ne 1 -or $missingResult[0].BadSlots -ne 1 -or
        -not $missingResult[0].PopulationComplete -or -not $missingResult[0].MeetsDraftTarget) {
        throw "A mature missing slot within the draft budget must remain bad without invalidating evidence."
    }
    $missingSummary = [IO.File]::ReadAllText($summaryPath)
    foreach ($required in @("Shadow result: passed", "Evidence integrity: passed",
            "Good/bad/missing slots: ``1439/1/1``", "Draft target at shadow scale: ``met``")) {
        if (-not $missingSummary.Contains($required, [StringComparison]::Ordinal)) {
            throw "Missing-slot shadow summary is missing '$required'."
        }
    }
    Assert-AuditDirectoriesCleaned

    foreach ($mode in @("wrong-slots", "pending", "malformed-counts")) {
        Remove-Item -LiteralPath $summaryPath -Force -ErrorAction SilentlyContinue
        $env:GOLDSRCOPS_FAKE_AUDIT_MODE = $mode
        Assert-Fails `
            -Operation { & $auditPath @arguments } `
            -ExpectedMessage "Shadow audit failed: identity matched=True; expected/evaluated/pending/missing slots=*"
        $failureSummary = [IO.File]::ReadAllText($summaryPath)
        if (-not $failureSummary.Contains("Evidence integrity: failed", [StringComparison]::Ordinal) -or
            -not $failureSummary.Contains("Identity matched: ``true``", [StringComparison]::Ordinal)) {
            throw "Incomplete shadow audit did not publish sanitized failure diagnostics."
        }
        Assert-AuditDirectoriesCleaned
    }

    Remove-Item -LiteralPath $summaryPath -Force -ErrorAction SilentlyContinue
    $env:GOLDSRCOPS_FAKE_AUDIT_MODE = "target-miss"
    Assert-Fails `
        -Operation { & $auditPath @arguments } `
        -ExpectedMessage "Shadow audit failed: draft target was not met; good/bad/missing slots=1432/8/8."
    $targetFailureSummary = [IO.File]::ReadAllText($summaryPath)
    foreach ($required in @("Shadow result: failed", "Evidence integrity: passed",
            "Draft target at shadow scale: ``missed``")) {
        if (-not $targetFailureSummary.Contains($required, [StringComparison]::Ordinal)) {
            throw "Target-failure shadow summary is missing '$required'."
        }
    }
    Assert-AuditDirectoriesCleaned

    $env:GOLDSRCOPS_FAKE_AUDIT_MODE = "export-fail"
    Assert-Fails `
        -Operation { & $auditPath @arguments } `
        -ExpectedMessage "Shadow export failed (metrics_http_503)*"
    Assert-AuditDirectoriesCleaned

    $env:GOLDSRCOPS_FAKE_AUDIT_MODE = "evaluate-fail"
    Assert-Fails `
        -Operation { & $auditPath @arguments } `
        -ExpectedMessage "Shadow evaluation failed*"
    Assert-AuditDirectoriesCleaned

    $env:GOLDSRCOPS_FAKE_AUDIT_MODE = "oversize"
    Assert-Fails `
        -Operation { & $auditPath @arguments } `
        -ExpectedMessage "*evidence size limit*"
    Assert-AuditDirectoriesCleaned

    $env:GOLDSRCOPS_FAKE_AUDIT_MODE = "success"
    $env:GOLDSRCOPS_GRAFANA_LOGS_TOKEN = ""
    Assert-Fails `
        -Operation { & $auditPath @arguments } `
        -ExpectedMessage "*GOLDSRCOPS_GRAFANA_LOGS_TOKEN*missing*"
    $env:GOLDSRCOPS_GRAFANA_LOGS_TOKEN = "smoke-GOLDSRCOPS_GRAFANA_LOGS_TOKEN"

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    foreach ($required in @(
            "workflow_dispatch:",
            "window_end_utc:",
            "github.ref == 'refs/heads/main'",
            "group: availability-shadow-audit-v1",
            "cancel-in-progress: false",
            "contents: read",
            "name: availability-evidence-shadow",
            "deployment: false",
            "persist-credentials: false",
            'ref: ${{ github.sha }}',
            'ref: ${{ vars.GOLDSRCOPS_AVAILABILITY_EXPORTER_REVISION }}',
            "git merge-base --is-ancestor",
            "run-shadow-audit.ps1",
            'WindowEndUtc $env:REQUESTED_WINDOW_END_UTC')) {
        if (-not $workflow.Contains($required, [StringComparison]::Ordinal)) {
            throw "Availability shadow audit workflow is missing required fragment '$required'."
        }
    }

    foreach ($forbidden in @("GOLDSRCOPS_B2", "upload-artifact", " archive ", "schedule:")) {
        if ($workflow.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Availability shadow audit workflow contains forbidden fragment '$forbidden'."
        }
    }

    $auditStepMarker = "      - name: Audit completed primary shadow window"
    $auditStepIndex = $workflow.IndexOf($auditStepMarker, [StringComparison]::Ordinal)
    if ($auditStepIndex -lt 0 -or
        $workflow.Substring(0, $auditStepIndex).Contains("secrets.", [StringComparison]::Ordinal)) {
        throw "Read credentials must reach only the shadow audit step."
    }

    $runBody = $workflow.Substring($workflow.LastIndexOf("        run: |", [StringComparison]::Ordinal))
    if ($runBody.Contains('${{ inputs.', [StringComparison]::Ordinal) -or
        $runBody.Contains('${{ secrets.', [StringComparison]::Ordinal)) {
        throw "Inputs and secrets must not be interpolated into executable workflow code."
    }

    foreach ($name in @(
            "GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE",
            "GOLDSRCOPS_GRAFANA_METRICS_URL",
            "GOLDSRCOPS_GRAFANA_METRICS_USER",
            "GOLDSRCOPS_GRAFANA_METRICS_TOKEN",
            "GOLDSRCOPS_GRAFANA_LOGS_URL",
            "GOLDSRCOPS_GRAFANA_LOGS_USER",
            "GOLDSRCOPS_GRAFANA_LOGS_TOKEN")) {
        $mapping = ('{0}: ${{{{ secrets.{0} }}}}' -f $name)
        if (-not $workflow.Contains($mapping, [StringComparison]::Ordinal)) {
            throw "Availability shadow audit workflow is missing environment mapping '$name'."
        }
    }

    Write-Host "Availability shadow audit smoke passed: complete 24-hour contract, missing-slot accounting, target enforcement, sanitization, and cleanup."
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }

    if ([IO.Directory]::Exists($root)) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedRoot = [IO.Path]::GetFullPath($root)
        $prefix = $temporaryRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a shadow-audit smoke directory outside the temporary root."
        }
        [IO.Directory]::Delete($resolvedRoot, $true)
    }
}
