#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$schedulerPath = Join-Path $PSScriptRoot "../../ops/availability/run-export-cycle.ps1"
$workflowPath = Join-Path $PSScriptRoot "../../.github/workflows/availability-evidence.yml"
$temporaryRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "goldsrcops-availability-schedule-$([Guid]::NewGuid().ToString('N'))"
$fakeExporterPath = Join-Path $temporaryRoot "fake-exporter.ps1"
$invocationLogPath = Join-Path $temporaryRoot "invocations.jsonl"
$workingDirectory = Join-Path $temporaryRoot "work"
$environmentNames = @(
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
    "GOLDSRCOPS_B2_READ_APPLICATION_KEY",
    "GOLDSRCOPS_FAKE_EXPORT_FAIL",
    "GOLDSRCOPS_FAKE_ARCHIVE_FAIL",
    "GOLDSRCOPS_FAKE_INVOCATION_LOG")
$previousEnvironment = @{}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Actual,

        [Parameter(Mandatory = $true)]
        [object]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    if ($Actual -cne $Expected) {
        throw "$Context Expected '$Expected', got '$Actual'."
    }
}

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
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $index = [Array]::IndexOf($Arguments, $Name)
    if ($index -lt 0 -or $index + 1 -ge $Arguments.Count) {
        throw "Argument '$Name' is missing."
    }

    return [string]$Arguments[$index + 1]
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

    $fakeExporter = @'
#Requires -Version 7.0
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArguments
)

$ErrorActionPreference = "Stop"
$encodedArguments = @(
    $CommandArguments |
    ForEach-Object {
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))
    })
[IO.File]::AppendAllText(
    $env:GOLDSRCOPS_FAKE_INVOCATION_LOG,
    (($encodedArguments -join ",") + [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false))

$command = $CommandArguments[0]
if ($command -eq "export") {
    if ($env:GOLDSRCOPS_FAKE_EXPORT_FAIL -eq "true") {
        throw "Synthetic export failure."
    }

    $outputIndex = [Array]::IndexOf($CommandArguments, "--output")
    $outputPath = $CommandArguments[$outputIndex + 1]
    $record = '{"scheduled_at_utc":"2026-09-04T09:59:00Z","started_at_utc":"2026-09-04T09:59:01Z","completed_at_utc":"2026-09-04T09:59:02Z","monitor_revision":"v2-4-shadow-001","location":"Frankfurt","role":"primary","execution_id":"smoke-1","outcome":"good","http_status":200,"duration_ms":100}'
    [IO.File]::WriteAllText($outputPath, $record + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    return
}

if ($command -eq "archive") {
    if ($env:GOLDSRCOPS_FAKE_ARCHIVE_FAIL -eq "true") {
        throw "Synthetic archive failure."
    }

    return
}

throw "Unexpected fake exporter command '$command'."
'@
    [IO.File]::WriteAllText(
        $fakeExporterPath,
        $fakeExporter,
        [Text.UTF8Encoding]::new($false))

    foreach ($name in $environmentNames) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }

    $env:GOLDSRCOPS_AVAILABILITY_PRIMARY_JOB = "goldsrcops-api-ready-primary-shadow"
    $env:GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE = "smoke-probe"
    $env:GOLDSRCOPS_AVAILABILITY_PRIMARY_LOCATION = "Frankfurt"
    $env:GOLDSRCOPS_AVAILABILITY_MONITOR_REVISION = "v2-4-shadow-001"
    $env:GOLDSRCOPS_GRAFANA_METRICS_URL = "https://metrics.invalid"
    $env:GOLDSRCOPS_GRAFANA_METRICS_USER = "metrics-user"
    $env:GOLDSRCOPS_GRAFANA_METRICS_TOKEN = "secret-metrics-token"
    $env:GOLDSRCOPS_B2_S3_ENDPOINT = "https://s3.invalid"
    $env:GOLDSRCOPS_B2_REGION = "test-region"
    $env:GOLDSRCOPS_B2_BUCKET = "test-bucket"
    $env:GOLDSRCOPS_B2_WRITE_KEY_ID = "writer-id"
    $env:GOLDSRCOPS_B2_WRITE_APPLICATION_KEY = "secret-writer-key"
    $env:GOLDSRCOPS_B2_READ_KEY_ID = "reader-id"
    $env:GOLDSRCOPS_B2_READ_APPLICATION_KEY = "secret-reader-key"
    $env:GOLDSRCOPS_FAKE_INVOCATION_LOG = $invocationLogPath

    $plan = & $schedulerPath `
        -ExporterAssemblyPath $fakeExporterPath `
        -WorkingDirectory $workingDirectory `
        -WindowEndUtc "2026-09-04T10:00:00Z" `
        -LookbackMinutes 360 `
        -PlanOnly
    Assert-Equal -Actual $plan.Status -Expected "Planned" -Context "Plan status mismatch."
    Assert-Equal `
        -Actual $plan.WindowStartUtc `
        -Expected "2026-09-04T04:00:00.0000000+00:00" `
        -Context "Plan start mismatch."
    Assert-Equal `
        -Actual $plan.WindowEndUtc `
        -Expected "2026-09-04T10:00:00.0000000+00:00" `
        -Context "Plan end mismatch."
    if (Test-Path -LiteralPath $invocationLogPath) {
        throw "Plan-only mode invoked the exporter."
    }

    Assert-Fails `
        -Operation {
            & $schedulerPath `
                -ExporterAssemblyPath $fakeExporterPath `
                -WorkingDirectory $workingDirectory `
                -WindowEndUtc "2026-09-04T10:00:00+03:00" `
                -PlanOnly
        } `
        -ExpectedMessage "*exact UTC-minute format*"
    Assert-Fails `
        -Operation {
            & $schedulerPath `
                -ExporterAssemblyPath $fakeExporterPath `
                -WorkingDirectory $workingDirectory `
                -WindowEndUtc "2099-01-01T00:00:00Z" `
                -PlanOnly
        } `
        -ExpectedMessage "*ingestion-delay boundary*"

    $pwshPath = (Get-Process -Id $PID).Path
    $result = & $schedulerPath `
        -ExporterAssemblyPath $fakeExporterPath `
        -WorkingDirectory $workingDirectory `
        -WindowEndUtc "2026-09-04T10:00:00Z" `
        -LookbackMinutes 360 `
        -DotNetExecutable $pwshPath |
        Where-Object { $_ -is [pscustomobject] -and $_.Action -eq "AvailabilityEvidenceExportCycle" }
    Assert-Equal -Actual $result.Status -Expected "Archived" -Context "Run status mismatch."

    $invocations = @(
        Get-Content -LiteralPath $invocationLogPath |
        ForEach-Object {
            , @(
                $_.Split(",", [StringSplitOptions]::RemoveEmptyEntries) |
                ForEach-Object {
                    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_))
                })
        })
    Assert-Equal -Actual $invocations.Count -Expected 2 -Context "Invocation count mismatch."
    Assert-Equal -Actual $invocations[0][0] -Expected "export" -Context "First command mismatch."
    Assert-Equal -Actual $invocations[1][0] -Expected "archive" -Context "Second command mismatch."
    Assert-Equal `
        -Actual (Get-ArgumentValue -Arguments $invocations[0] -Name "--window-start") `
        -Expected "2026-09-04T04:00:00.0000000+00:00" `
        -Context "Export start mismatch."
    Assert-Equal `
        -Actual (Get-ArgumentValue -Arguments $invocations[0] -Name "--window-end") `
        -Expected "2026-09-04T10:00:00.0000000+00:00" `
        -Context "Export end mismatch."
    Assert-Equal `
        -Actual (Get-ArgumentValue -Arguments $invocations[0] -Name "--overlap-minutes") `
        -Expected "10" `
        -Context "Export overlap mismatch."
    Assert-Equal `
        -Actual (Get-ArgumentValue -Arguments $invocations[0] -Name "--step-seconds") `
        -Expected "15" `
        -Context "Export step mismatch."

    $environmentOnlyValues = @(
        $env:GOLDSRCOPS_GRAFANA_METRICS_URL,
        $env:GOLDSRCOPS_GRAFANA_METRICS_USER,
        $env:GOLDSRCOPS_GRAFANA_METRICS_TOKEN,
        $env:GOLDSRCOPS_B2_S3_ENDPOINT,
        $env:GOLDSRCOPS_B2_REGION,
        $env:GOLDSRCOPS_B2_BUCKET,
        $env:GOLDSRCOPS_B2_WRITE_KEY_ID,
        $env:GOLDSRCOPS_B2_WRITE_APPLICATION_KEY,
        $env:GOLDSRCOPS_B2_READ_KEY_ID,
        $env:GOLDSRCOPS_B2_READ_APPLICATION_KEY)
    foreach ($invocation in $invocations) {
        foreach ($argument in $invocation) {
            foreach ($environmentOnlyValue in $environmentOnlyValues) {
                if ([string]::Equals(
                        $argument,
                        $environmentOnlyValue,
                        [StringComparison]::Ordinal)) {
                    throw "An environment-only value was forwarded through command-line arguments."
                }
            }
        }
    }

    if (@(Get-ChildItem -LiteralPath $workingDirectory -Force).Count -ne 0) {
        throw "The scheduler retained a temporary evidence segment."
    }

    $successfulInvocationCount = $invocations.Count
    $savedMetricsToken = $env:GOLDSRCOPS_GRAFANA_METRICS_TOKEN
    $env:GOLDSRCOPS_GRAFANA_METRICS_TOKEN = ""
    Assert-Fails `
        -Operation {
            & $schedulerPath `
                -ExporterAssemblyPath $fakeExporterPath `
                -WorkingDirectory $workingDirectory `
                -WindowEndUtc "2026-09-04T10:00:00Z" `
                -DotNetExecutable $pwshPath
        } `
        -ExpectedMessage "*GOLDSRCOPS_GRAFANA_METRICS_TOKEN*missing*"
    $env:GOLDSRCOPS_GRAFANA_METRICS_TOKEN = $savedMetricsToken
    Assert-Equal `
        -Actual @(Get-Content -LiteralPath $invocationLogPath).Count `
        -Expected $successfulInvocationCount `
        -Context "Missing-secret invocation count mismatch."

    $env:GOLDSRCOPS_FAKE_EXPORT_FAIL = "true"
    Assert-Fails `
        -Operation {
            & $schedulerPath `
                -ExporterAssemblyPath $fakeExporterPath `
                -WorkingDirectory $workingDirectory `
                -WindowEndUtc "2026-09-04T10:00:00Z" `
                -DotNetExecutable $pwshPath
        } `
        -ExpectedMessage "*Availability exporter command failed*"
    $env:GOLDSRCOPS_FAKE_EXPORT_FAIL = ""
    Assert-Equal `
        -Actual @(Get-Content -LiteralPath $invocationLogPath).Count `
        -Expected ($successfulInvocationCount + 1) `
        -Context "Export-failure invocation count mismatch."
    if (@(Get-ChildItem -LiteralPath $workingDirectory -Force).Count -ne 0) {
        throw "The scheduler retained evidence after an export failure."
    }

    $env:GOLDSRCOPS_FAKE_ARCHIVE_FAIL = "true"
    Assert-Fails `
        -Operation {
            & $schedulerPath `
                -ExporterAssemblyPath $fakeExporterPath `
                -WorkingDirectory $workingDirectory `
                -WindowEndUtc "2026-09-04T10:00:00Z" `
                -DotNetExecutable $pwshPath
        } `
        -ExpectedMessage "*Availability exporter command failed*"
    $env:GOLDSRCOPS_FAKE_ARCHIVE_FAIL = ""
    Assert-Equal `
        -Actual @(Get-Content -LiteralPath $invocationLogPath).Count `
        -Expected ($successfulInvocationCount + 3) `
        -Context "Archive-failure invocation count mismatch."
    if (@(Get-ChildItem -LiteralPath $workingDirectory -Force).Count -ne 0) {
        throw "The scheduler retained evidence after an archive failure."
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $requiredWorkflowFragments = @(
        'cron: "17 * * * *"',
        "confirm_archive:",
        "inputs.confirm_archive",
        "vars.GOLDSRCOPS_AVAILABILITY_SCHEDULER_ENABLED == 'true'",
        "group: availability-evidence-writer-v1",
        "cancel-in-progress: false",
        "name: availability-evidence-shadow",
        "deployment: false",
        "contents: read",
        "persist-credentials: false",
        "git merge-base --is-ancestor",
        'ref: ${{ vars.GOLDSRCOPS_AVAILABILITY_EXPORTER_REVISION }}',
        "GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE: `${{ secrets.GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE }}",
        "./ops/availability/run-export-cycle.ps1 @arguments")
    foreach ($fragment in $requiredWorkflowFragments) {
        if (-not $workflow.Contains($fragment, [StringComparison]::Ordinal)) {
            throw "Availability evidence workflow is missing required fragment '$fragment'."
        }
    }

    if ($workflow.Contains("upload-artifact", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Availability evidence workflow must not retain raw GitHub artifacts."
    }

    Write-Host "Availability evidence schedule smoke passed: plan, success, secret boundary, failure paths, cleanup, and workflow contract."
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }

    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedDirectory = [IO.Path]::GetFullPath($temporaryRoot)
        if (-not $resolvedDirectory.StartsWith(
                $resolvedTemporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a schedule smoke directory outside the temporary path."
        }

        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}
