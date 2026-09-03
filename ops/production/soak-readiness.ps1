#Requires -Version 7.0

<#
.SYNOPSIS
Evaluates the control-plane portion of a GoldSrcOps release soak.

.DESCRIPTION
Compares an owner-only soak baseline with read-only runtime, database,
telemetry, backup, and host observations. Live mode never changes the runtime.
Snapshot mode exists for deterministic CI validation and is always marked as
non-target evidence.

The script intentionally does not claim continuous public API availability.
The production metrics path observes the OTLP pipeline, while public health is
sampled separately by the operator heartbeat.
#>

[CmdletBinding(DefaultParameterSetName = "Live")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Live")]
    [Parameter(Mandatory = $true, ParameterSetName = "Snapshot")]
    [ValidateNotNullOrEmpty()]
    [string]$BaselineFile,

    [Parameter(Mandatory = $true, ParameterSetName = "Live")]
    [ValidateNotNullOrEmpty()]
    [string]$EnvironmentFile,

    [Parameter(Mandatory = $true, ParameterSetName = "Snapshot")]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotFile,

    [string]$EvidenceFile,

    [ValidateRange(1, 168)]
    [int]$ExpectedDurationHours = 24,

    [ValidateRange(1, 60)]
    [int]$PollFreshnessMinutes = 2,

    [ValidateRange(5, 3600)]
    [int]$ExpectedPollIntervalSeconds = 60,

    [ValidateRange(1, 100)]
    [double]$MinimumPollCoveragePercent = 98,

    [ValidateRange(1, 100)]
    [double]$MinimumTelemetryCoveragePercent = 99,

    [ValidateRange(1, 1024)]
    [int]$MinimumFreeDiskGiB = 10,

    [ValidateRange(128, 1048576)]
    [int]$MinimumAvailableMemoryMiB = 256,

    [switch]$AllowIncomplete
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$requiredServices = @(
    "api",
    "caddy",
    "grafana",
    "otel-collector",
    "postgres",
    "prometheus"
)
$checks = [Collections.Generic.List[object]]::new()

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

function ConvertTo-UtcTimestamp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $timestamp = [DateTimeOffset]::MinValue
    Assert-Condition `
        -Condition ([DateTimeOffset]::TryParse(
                $Value,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$timestamp)) `
        -Message "$Description must be an ISO 8601 timestamp with an offset."
    return $timestamp.ToUniversalTime()
}

function Add-Check {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [bool]$Passed,

        [Parameter(Mandatory = $true)]
        [string]$Detail
    )

    $checks.Add([ordered]@{
            Name = $Name
            Passed = $Passed
            Detail = $Detail
        })
}

function Invoke-NativeProbe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$Arguments = @()
    )

    if ($null -eq (Get-Command $FilePath -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{
            ExitCode = 127
            Output = ""
        }
    }

    try {
        $output = @(& $FilePath @Arguments 2>&1)
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
        }
    }
    catch {
        return [pscustomobject]@{
            ExitCode = 1
            Output = ""
        }
    }
}

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    try {
        return Get-Content -LiteralPath $resolvedPath -Raw |
            ConvertFrom-Json -Depth 100
    }
    catch {
        throw "$Description is not valid JSON."
    }
}

function Test-PathInsideRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $repoRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    return $fullPath -eq $repoRoot -or
        $fullPath.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-OwnerOnlyFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not $IsLinux) {
        return
    }

    $mode = [IO.File]::GetUnixFileMode($Path)
    $forbiddenMode =
        [IO.UnixFileMode]::GroupRead -bor
        [IO.UnixFileMode]::GroupWrite -bor
        [IO.UnixFileMode]::GroupExecute -bor
        [IO.UnixFileMode]::OtherRead -bor
        [IO.UnixFileMode]::OtherWrite -bor
        [IO.UnixFileMode]::OtherExecute
    Assert-Condition `
        -Condition (($mode -band $forbiddenMode) -eq 0) `
        -Message "$Description must not be accessible by group or other users."
}

function Write-SoakEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$Evidence,

        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (Test-PathInsideRepository -Path $fullPath) {
        throw "Operational evidence must be written outside the repository."
    }

    $directory = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $directory)
    }

    $temporaryPath = "$fullPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText(
            $temporaryPath,
            ($Evidence | ConvertTo-Json -Depth 20),
            [Text.UTF8Encoding]::new($false))
        if ($IsLinux) {
            [IO.File]::SetUnixFileMode(
                $temporaryPath,
                [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        }
        [IO.File]::Move($temporaryPath, $fullPath, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Read-EnvironmentValues {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $values = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)

    foreach ($line in [IO.File]::ReadAllLines($resolvedPath)) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }

        $separator = $trimmed.IndexOf('=')
        Assert-Condition `
            -Condition ($separator -gt 0) `
            -Message "Deployment environment entries must use NAME=VALUE syntax."
        $name = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim()
        Assert-Condition `
            -Condition ($values.TryAdd($name, $value)) `
            -Message "Deployment environment contains a duplicate setting."
    }

    return $values
}

function Get-RequiredEnvironmentValue {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.Dictionary[string, string]]$Values,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $value = $null
    Assert-Condition `
        -Condition ($Values.TryGetValue($Name, [ref]$value) -and
            -not [string]::IsNullOrWhiteSpace($value)) `
        -Message "Deployment environment is missing '$Name'."
    return $value
}

function Invoke-ComposeProbe {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ComposeArguments,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    $probe = Invoke-NativeProbe -FilePath "docker" -Arguments ($ComposeArguments + $Arguments)
    Assert-Condition -Condition ($probe.ExitCode -eq 0) -Message $FailureMessage
    return $probe.Output
}

function Invoke-PrometheusScalar {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ComposeArguments,

        [Parameter(Mandatory = $true)]
        [string]$Query
    )

    $encodedQuery = [Uri]::EscapeDataString($Query)
    $uri = "http://127.0.0.1:9090/api/v1/query?query=$encodedQuery"
    $output = Invoke-ComposeProbe `
        -ComposeArguments $ComposeArguments `
        -Arguments @(
            "exec",
            "-T",
            "prometheus",
            "wget",
            "-qO-",
            "-T",
            "10",
            $uri
        ) `
        -FailureMessage "Prometheus query failed."

    try {
        $response = $output | ConvertFrom-Json -Depth 30
    }
    catch {
        throw "Prometheus returned invalid JSON."
    }
    Assert-Condition `
        -Condition ([string]$response.status -eq "success") `
        -Message "Prometheus query did not succeed."
    $results = @($response.data.result)
    Assert-Condition `
        -Condition ($results.Count -eq 1) `
        -Message "Prometheus query did not return exactly one series."

    $value = 0.0
    Assert-Condition `
        -Condition ([double]::TryParse(
                [string]$results[0].value[1],
                [Globalization.NumberStyles]::Float,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$value)) `
        -Message "Prometheus query returned a non-numeric value."
    return $value
}

function Get-LiveObservation {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Baseline,

        [Parameter(Mandatory = $true)]
        [string]$DeploymentEnvironmentFile
    )

    Assert-Condition -Condition $IsLinux -Message "Live soak evaluation requires Linux."
    $effectiveUserId = Invoke-NativeProbe -FilePath "id" -Arguments @("-u")
    Assert-Condition `
        -Condition ($effectiveUserId.ExitCode -eq 0 -and
            $effectiveUserId.Output.Trim() -ceq "0") `
        -Message "Live soak evaluation must run as root."

    $values = Read-EnvironmentValues -Path $DeploymentEnvironmentFile
    $hostname = Get-RequiredEnvironmentValue -Values $values -Name "GOLDSRCOPS_HOSTNAME"
    $apiImageReference = Get-RequiredEnvironmentValue `
        -Values $values `
        -Name "GOLDSRCOPS_IMAGE"
    $apiImageMatch = [regex]::Match(
        $apiImageReference,
        '@(?<digest>sha256:[0-9a-f]{64})\z',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    Assert-Condition `
        -Condition $apiImageMatch.Success `
        -Message "GOLDSRCOPS_IMAGE must be pinned by a lowercase SHA-256 digest."
    $apiImageDigest = $apiImageMatch.Groups["digest"].Value
    $composeArguments = @(
        "compose",
        "--env-file", (Resolve-Path -LiteralPath $DeploymentEnvironmentFile).Path,
        "--file", (Join-Path $PSScriptRoot "compose.yml")
    )

    $services = foreach ($service in $requiredServices) {
        $containerId = (Invoke-ComposeProbe `
                -ComposeArguments $composeArguments `
                -Arguments @("ps", "--quiet", $service) `
                -FailureMessage "Could not inspect required runtime services.").Trim()
        Assert-Condition `
            -Condition (-not [string]::IsNullOrWhiteSpace($containerId)) `
            -Message "A required runtime service has no container."

        $inspectionOutput = Invoke-NativeProbe -FilePath "docker" -Arguments @("inspect", $containerId)
        Assert-Condition `
            -Condition ($inspectionOutput.ExitCode -eq 0) `
            -Message "Could not inspect a required runtime container."
        $inspection = @($inspectionOutput.Output | ConvertFrom-Json -Depth 50)[0]
        $healthProperty = $inspection.State.PSObject.Properties["Health"]
        $health = if ($null -eq $healthProperty) {
            "not-configured"
        }
        else {
            [string]$healthProperty.Value.Status
        }

        [ordered]@{
            Service = $service
            Running = [bool]$inspection.State.Running
            Image = [string]$inspection.Config.Image
            ImageDigest = [string]$inspection.Image
            StartedAtUtc = ConvertTo-UtcTimestamp `
                -Value ([string]$inspection.State.StartedAt) `
                -Description "Container start time"
            RestartCount = [int]$inspection.RestartCount
            Health = $health
        }
    }

    $apiContainerId = (Invoke-ComposeProbe `
            -ComposeArguments $composeArguments `
            -Arguments @("ps", "--quiet", "api") `
            -FailureMessage "Could not inspect the API container.").Trim()
    $apiInspectionProbe = Invoke-NativeProbe -FilePath "docker" -Arguments @("inspect", $apiContainerId)
    Assert-Condition `
        -Condition ($apiInspectionProbe.ExitCode -eq 0) `
        -Message "Could not inspect the API candidate."
    $apiInspection = @($apiInspectionProbe.Output | ConvertFrom-Json -Depth 50)[0]

    $resolve = "$($hostname):443:127.0.0.1"
    $liveness = Invoke-NativeProbe -FilePath "curl" -Arguments @(
        "--silent",
        "--show-error",
        "--connect-timeout", "5",
        "--max-time", "10",
        "--output", "/dev/null",
        "--write-out", "%{http_code}",
        "--resolve", $resolve,
        "https://$hostname/health/live"
    )
    $readiness = Invoke-NativeProbe -FilePath "curl" -Arguments @(
        "--silent",
        "--show-error",
        "--connect-timeout", "5",
        "--max-time", "10",
        "--output", "/dev/null",
        "--write-out", "%{http_code}",
        "--resolve", $resolve,
        "https://$hostname/health/ready"
    )

    $backupFresh = $true
    try {
        & (Join-Path $PSScriptRoot "postgres-backup-status.ps1") `
            -EnvironmentFile $DeploymentEnvironmentFile `
            -StatusFile "/var/lib/goldsrcops/evidence/postgres-backup-cycle.json" `
            -Kind ScheduledCycle `
            -MaximumAgeHours 36 *> $null
    }
    catch {
        $backupFresh = $false
    }

    $soakStartedAt = ConvertTo-UtcTimestamp `
        -Value ([string]$Baseline.StartedAtUtc) `
        -Description "Soak start time"
    $startLiteral = $soakStartedAt.ToString(
        "yyyy-MM-ddTHH:mm:ss.fffffffZ",
        [Globalization.CultureInfo]::InvariantCulture)
    $sql = @"
WITH window_snapshots AS (
    SELECT *
    FROM goldsrcops.poll_snapshots
    WHERE "CheckedAtUtc" >= TIMESTAMPTZ '$startLiteral'
),
ordered_snapshots AS (
    SELECT
        "ServerId",
        "CheckedAtUtc",
        lag("CheckedAtUtc") OVER (
            PARTITION BY "ServerId"
            ORDER BY "CheckedAtUtc", "Id") AS previous_checked_at
    FROM window_snapshots
)
SELECT json_build_object(
    'migrationRevision', (
        SELECT "MigrationId"
        FROM "__EFMigrationsHistory"
        ORDER BY "MigrationId" DESC
        LIMIT 1
    ),
    'serverCount', (SELECT count(*) FROM goldsrcops.servers),
    'enabledServerCount', (
        SELECT count(*) FROM goldsrcops.servers WHERE "IsEnabled"
    ),
    'onlineServerCount', (
        SELECT count(*)
        FROM goldsrcops.server_current_states
        WHERE "Status" = 'Online' AND "IsReachable"
    ),
    'pollIntervalSeconds', (
        SELECT min("PollIntervalSeconds")
        FROM goldsrcops.servers
        WHERE "IsEnabled"
    ),
    'latestPollAtUtc', (
        SELECT "CheckedAtUtc"
        FROM goldsrcops.poll_snapshots
        ORDER BY "CheckedAtUtc" DESC, "Id" DESC
        LIMIT 1
    ),
    'pollTotal', (SELECT count(*) FROM window_snapshots),
    'pollSuccessful', (
        SELECT count(*) FROM window_snapshots WHERE "IsReachable"
    ),
    'pollFailed', (
        SELECT count(*) FROM window_snapshots WHERE NOT "IsReachable"
    ),
    'botPositivePolls', (
        SELECT count(*) FROM window_snapshots WHERE coalesce("Bots", 0) > 0
    ),
    'latencySampleCount', (
        SELECT count(*) FROM window_snapshots WHERE "LatencyMs" IS NOT NULL
    ),
    'averageLatencyMs', (
        SELECT round(avg("LatencyMs")::numeric, 2)
        FROM window_snapshots
        WHERE "LatencyMs" IS NOT NULL
    ),
    'p95LatencyMs', (
        SELECT round((percentile_cont(0.95) WITHIN GROUP (
            ORDER BY "LatencyMs"))::numeric, 2)
        FROM window_snapshots
        WHERE "LatencyMs" IS NOT NULL
    ),
    'maximumLatencyMs', (
        SELECT max("LatencyMs")
        FROM window_snapshots
    ),
    'maximumPollGapSeconds', (
        SELECT round(max(extract(epoch FROM (
            "CheckedAtUtc" - previous_checked_at)))::numeric, 2)
        FROM ordered_snapshots
        WHERE previous_checked_at IS NOT NULL
    ),
    'incidentsOpened', (
        SELECT count(*)
        FROM goldsrcops.availability_incidents
        WHERE "OpenedAtUtc" >= TIMESTAMPTZ '$startLiteral'
    ),
    'openIncidentCount', (
        SELECT count(*)
        FROM goldsrcops.availability_incidents
        WHERE "ClosedAtUtc" IS NULL
    ),
    'maximumIncidentDurationSeconds', (
        SELECT round(max(extract(epoch FROM (
            coalesce("ClosedAtUtc", now()) - "OpenedAtUtc")))::numeric, 2)
        FROM goldsrcops.availability_incidents
        WHERE "OpenedAtUtc" >= TIMESTAMPTZ '$startLiteral'
    ),
    'commandsTotal', (
        SELECT count(*)
        FROM goldsrcops.command_executions
        WHERE "RequestedAtUtc" >= TIMESTAMPTZ '$startLiteral'
    ),
    'commandsSucceeded', (
        SELECT count(*)
        FROM goldsrcops.command_executions
        WHERE "RequestedAtUtc" >= TIMESTAMPTZ '$startLiteral'
          AND "Status" = 'Succeeded'
    ),
    'commandsFailed', (
        SELECT count(*)
        FROM goldsrcops.command_executions
        WHERE "RequestedAtUtc" >= TIMESTAMPTZ '$startLiteral'
          AND "Status" = 'Failed'
    ),
    'incompleteCommandCount', (
        SELECT count(*)
        FROM goldsrcops.command_executions
        WHERE "Status" IN ('Pending', 'Running')
    ),
    'outboxEventsTotal', (
        SELECT count(*)
        FROM goldsrcops.outbox_messages
        WHERE "OccurredAtUtc" >= TIMESTAMPTZ '$startLiteral'
    ),
    'outboxEventsProcessed', (
        SELECT count(*)
        FROM goldsrcops.outbox_messages
        WHERE "OccurredAtUtc" >= TIMESTAMPTZ '$startLiteral'
          AND "Status" = 'Processed'
    ),
    'pendingOutboxCount', (
        SELECT count(*)
        FROM goldsrcops.outbox_messages
        WHERE "Status" IN ('Pending', 'Processing')
    ),
    'deadLetterCount', (
        SELECT count(*)
        FROM goldsrcops.outbox_messages
        WHERE "Status" = 'DeadLetter'
    ),
    'databaseSizeBytes', pg_database_size(current_database()),
    'pollSnapshotsTableSizeBytes', pg_total_relation_size(
        'goldsrcops.poll_snapshots')
);
"@
    $databaseCommand = @(
        'PGCONNECT_TIMEOUT=5'
        'PGPASSWORD="$(cat /run/secrets/postgres-password)"'
        'PGOPTIONS="-c statement_timeout=15000 -c lock_timeout=5000"'
        'exec psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB"'
        '--tuples-only --no-align --quiet --set ON_ERROR_STOP=1'
    ) -join ' '
    $databaseJson = ($sql |
        & docker @composeArguments exec -T postgres sh -lc $databaseCommand 2>$null |
            Out-String).Trim()
    Assert-Condition `
        -Condition ($LASTEXITCODE -eq 0 -and
            -not [string]::IsNullOrWhiteSpace($databaseJson)) `
        -Message "Could not collect the database soak observation."
    try {
        $database = $databaseJson | ConvertFrom-Json -Depth 30
    }
    catch {
        throw "PostgreSQL returned invalid soak observation JSON."
    }

    $collectedAtUtc = [DateTimeOffset]::UtcNow
    $elapsedSeconds = [Math]::Max(
        60,
        [Math]::Floor(($collectedAtUtc - $soakStartedAt).TotalSeconds))
    $rangeSelector = "${elapsedSeconds}s"
    $goldSrcOpsUp = Invoke-PrometheusScalar `
        -ComposeArguments $composeArguments `
        -Query 'up{job="goldsrcops"}'
    $collectorUp = Invoke-PrometheusScalar `
        -ComposeArguments $composeArguments `
        -Query 'up{job="otel-collector"}'
    $goldSrcOpsSamples = Invoke-PrometheusScalar `
        -ComposeArguments $composeArguments `
        -Query "count_over_time(up{job=`"goldsrcops`"}[$rangeSelector])"
    $goldSrcOpsHealthySamples = Invoke-PrometheusScalar `
        -ComposeArguments $composeArguments `
        -Query "sum_over_time(up{job=`"goldsrcops`"}[$rangeSelector])"
    $collectorSamples = Invoke-PrometheusScalar `
        -ComposeArguments $composeArguments `
        -Query "count_over_time(up{job=`"otel-collector`"}[$rangeSelector])"
    $collectorHealthySamples = Invoke-PrometheusScalar `
        -ComposeArguments $composeArguments `
        -Query "sum_over_time(up{job=`"otel-collector`"}[$rangeSelector])"

    $disk = Invoke-NativeProbe -FilePath "df" -Arguments @(
        "--block-size=1",
        "--output=avail",
        "/var/lib/docker"
    )
    Assert-Condition -Condition ($disk.ExitCode -eq 0) -Message "Could not inspect free disk."
    $diskValues = @($disk.Output -split "`n" | ForEach-Object { $_.Trim() } |
            Where-Object { $_ -match '\A\d+\z' })
    Assert-Condition `
        -Condition ($diskValues.Count -gt 0) `
        -Message "Free-disk observation is invalid."

    $memory = Invoke-NativeProbe -FilePath "free" -Arguments @("--bytes")
    Assert-Condition -Condition ($memory.ExitCode -eq 0) -Message "Could not inspect memory."
    $memoryLine = @($memory.Output -split "`n" | Where-Object { $_ -match '^Mem:' })
    Assert-Condition `
        -Condition ($memoryLine.Count -eq 1) `
        -Message "Memory observation is invalid."
    $memoryColumns = @($memoryLine[0] -split '\s+' | Where-Object { $_ })
    Assert-Condition `
        -Condition ($memoryColumns.Count -ge 7) `
        -Message "Memory observation is incomplete."

    $processorCountProbe = Invoke-NativeProbe -FilePath "nproc"
    Assert-Condition `
        -Condition ($processorCountProbe.ExitCode -eq 0 -and
            $processorCountProbe.Output.Trim() -match '\A\d+\z') `
        -Message "Processor-count observation is invalid."
    $loadAverage = (Get-Content -LiteralPath "/proc/loadavg" -Raw).Split(' ')[0]

    return [pscustomobject][ordered]@{
        SchemaVersion = 1
        CollectedAtUtc = $collectedAtUtc
        Candidate = [ordered]@{
            Version = [string]$apiInspection.Config.Labels."org.opencontainers.image.version"
            SourceRevision = [string]$apiInspection.Config.Labels."org.opencontainers.image.revision"
            ApiImageDigest = $apiImageDigest
            MigrationRevision = [string]$database.migrationRevision
        }
        Services = @($services)
        Edge = [ordered]@{
            LivenessHttpStatus = if ($liveness.ExitCode -eq 0) { [int]$liveness.Output } else { 0 }
            ReadinessHttpStatus = if ($readiness.ExitCode -eq 0) { [int]$readiness.Output } else { 0 }
            AlertDeliveryEnabled = $apiInspection.Config.Env -notcontains "AlertDelivery__Enabled=false"
        }
        Backup = [ordered]@{
            Fresh = $backupFresh
            MaximumAgeHours = 36
        }
        Database = $database
        Telemetry = [ordered]@{
            ScrapeIntervalSeconds = 15
            GoldSrcOpsUp = $goldSrcOpsUp
            GoldSrcOpsSamples = $goldSrcOpsSamples
            GoldSrcOpsHealthySamples = $goldSrcOpsHealthySamples
            CollectorUp = $collectorUp
            CollectorSamples = $collectorSamples
            CollectorHealthySamples = $collectorHealthySamples
        }
        Host = [ordered]@{
            FreeDiskBytes = [long]$diskValues[-1]
            AvailableMemoryBytes = [long]$memoryColumns[6]
            ProcessorCount = [int]$processorCountProbe.Output.Trim()
            LoadAverageOneMinute = [double]::Parse(
                $loadAverage,
                [Globalization.CultureInfo]::InvariantCulture)
        }
    }
}

$resolvedBaseline = (Resolve-Path -LiteralPath $BaselineFile).Path
if ($PSCmdlet.ParameterSetName -eq "Live") {
    Assert-Condition `
        -Condition (-not (Test-PathInsideRepository -Path $resolvedBaseline)) `
        -Message "The live soak baseline must remain outside the repository."
    Assert-OwnerOnlyFile -Path $resolvedBaseline -Description "The live soak baseline"
}
$baseline = Read-JsonFile -Path $resolvedBaseline -Description "Soak baseline"

Assert-Condition `
    -Condition ([int]$baseline.SchemaVersion -eq 1 -and
        [string]$baseline.Action -eq "V23SoakBaseline") `
    -Message "Unsupported soak baseline contract."
Assert-Condition `
    -Condition ([int]$baseline.RequiredDurationHours -eq $ExpectedDurationHours) `
    -Message "Soak baseline duration does not match the expected release gate."
Assert-Condition `
    -Condition (-not [string]::IsNullOrWhiteSpace(
            [string]$baseline.Candidate.Version) -and
        [string]$baseline.Candidate.SourceRevision -cmatch '\A[0-9a-f]{40}\z' -and
        [string]$baseline.Candidate.ApiImageDigest -cmatch
            '\Asha256:[0-9a-f]{64}\z' -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$baseline.Candidate.MigrationRevision)) `
    -Message "Soak baseline candidate identity is invalid."

$startedAtUtc = ConvertTo-UtcTimestamp `
    -Value ([string]$baseline.StartedAtUtc) `
    -Description "Soak baseline start time"
$expectedCompletionAtUtc = ConvertTo-UtcTimestamp `
    -Value ([string]$baseline.ExpectedCompletionAtUtc) `
    -Description "Soak baseline completion time"
$calculatedCompletionAtUtc = $startedAtUtc.AddHours($ExpectedDurationHours)
Assert-Condition `
    -Condition ([Math]::Abs(
            ($expectedCompletionAtUtc - $calculatedCompletionAtUtc).TotalSeconds) -le 1) `
    -Message "Soak baseline completion time does not match its duration."

if ($PSCmdlet.ParameterSetName -eq "Live") {
    $observation = Get-LiveObservation `
        -Baseline $baseline `
        -DeploymentEnvironmentFile $EnvironmentFile
    $source = "Live"
    $targetEvidence = $true
}
else {
    $observation = Read-JsonFile -Path $SnapshotFile -Description "Soak observation snapshot"
    $source = "Snapshot"
    $targetEvidence = $false
}

Assert-Condition `
    -Condition ([int]$observation.SchemaVersion -eq 1) `
    -Message "Unsupported soak observation schema version."
$collectedAtUtc = ConvertTo-UtcTimestamp `
    -Value ([string]$observation.CollectedAtUtc) `
    -Description "Soak observation time"
Assert-Condition `
    -Condition ($collectedAtUtc -ge $startedAtUtc) `
    -Message "Soak observation predates its baseline."

$elapsed = $collectedAtUtc - $startedAtUtc
$durationComplete = $elapsed -ge [TimeSpan]::FromHours($ExpectedDurationHours)
if ($durationComplete) {
    Add-Check -Name "Soak duration" -Passed $true -Detail (
        "The {0}-hour release soak is complete." -f $ExpectedDurationHours)
}
elseif ($AllowIncomplete) {
    Add-Check -Name "Soak duration" -Passed $true -Detail (
        "The release soak is in progress at {0:N2} hours." -f $elapsed.TotalHours)
}
else {
    Add-Check -Name "Soak duration" -Passed $false -Detail (
        "Only {0:N2} of {1} required hours have elapsed." -f
            $elapsed.TotalHours, $ExpectedDurationHours)
}

$candidateMatches =
    [string]$observation.Candidate.Version -ceq [string]$baseline.Candidate.Version -and
    [string]$observation.Candidate.SourceRevision -ceq [string]$baseline.Candidate.SourceRevision -and
    [string]$observation.Candidate.ApiImageDigest -ceq [string]$baseline.Candidate.ApiImageDigest -and
    [string]$observation.Candidate.MigrationRevision -ceq [string]$baseline.Candidate.MigrationRevision
Add-Check -Name "Candidate identity" -Passed $candidateMatches -Detail $(
    if ($candidateMatches) {
        "Version, source revision, API digest, and migration revision match the baseline."
    }
    else {
        "The deployed candidate identity drifted from the baseline."
    })

$runtimeContinuity = $true
foreach ($serviceName in $requiredServices) {
    $baselineServices = @($baseline.ControlPlane.Services | Where-Object {
            [string]$_.Service -ceq $serviceName
        })
    $observedServices = @($observation.Services | Where-Object {
            [string]$_.Service -ceq $serviceName
        })
    if ($baselineServices.Count -ne 1 -or $observedServices.Count -ne 1) {
        $runtimeContinuity = $false
        continue
    }

    $baselineService = $baselineServices[0]
    $observedService = $observedServices[0]
    $observedServiceStartedAt = ConvertTo-UtcTimestamp `
        -Value ([string]$observedService.StartedAtUtc) `
        -Description "Observed container start time"
    $baselineServiceStartedAt = ConvertTo-UtcTimestamp `
        -Value ([string]$baselineService.StartedAtUtc) `
        -Description "Baseline container start time"
    $startedAtMatches = $observedServiceStartedAt -eq $baselineServiceStartedAt
    if (-not [bool]$observedService.Running -or
        [string]$observedService.Health -notin @("healthy", "not-configured") -or
        [string]$observedService.Image -cne [string]$baselineService.Image -or
        [int]$observedService.RestartCount -ne [int]$baselineService.RestartCount -or
        -not $startedAtMatches) {
        $runtimeContinuity = $false
    }
}
Add-Check -Name "Runtime continuity" -Passed $runtimeContinuity -Detail $(
    if ($runtimeContinuity) {
        "All required containers preserve their image, start time, restart count, and health."
    }
    else {
        "A required container changed or is not healthy."
    })

$edgeHealthy = [int]$observation.Edge.LivenessHttpStatus -eq 200 -and
    [int]$observation.Edge.ReadinessHttpStatus -eq 200
Add-Check -Name "Current edge health" -Passed $edgeHealthy -Detail $(
    if ($edgeHealthy) {
        "Current liveness and readiness both return HTTP 200."
    }
    else {
        "Current liveness or readiness is not HTTP 200."
    })
Add-Check `
    -Name "Alert delivery baseline" `
    -Passed (-not [bool]$observation.Edge.AlertDeliveryEnabled) `
    -Detail $(if (-not [bool]$observation.Edge.AlertDeliveryEnabled) {
            "Alert delivery remains disabled after the controlled exercise."
        }
        else {
            "Alert delivery is unexpectedly enabled."
        })
Add-Check `
    -Name "Backup freshness" `
    -Passed ([bool]$observation.Backup.Fresh) `
    -Detail $(if ([bool]$observation.Backup.Fresh) {
            "Scheduled backup evidence is within the configured freshness bound."
        }
        else {
            "Scheduled backup evidence is stale or invalid."
        })

$database = $observation.Database
$pollIntervalSeconds = [int]$database.pollIntervalSeconds
Assert-Condition `
    -Condition ($pollIntervalSeconds -gt 0) `
    -Message "The observed poll interval must be positive."
$pollConfigurationMatches = $pollIntervalSeconds -eq $ExpectedPollIntervalSeconds
Add-Check `
    -Name "Polling configuration" `
    -Passed $pollConfigurationMatches `
    -Detail $(if ($pollConfigurationMatches) {
            "The poll interval remains $ExpectedPollIntervalSeconds seconds."
        }
        else {
            "The poll interval differs from the reviewed release input."
        })
$serverStateHealthy = [int]$database.serverCount -eq 1 -and
    [int]$database.enabledServerCount -eq 1 -and
    [int]$database.onlineServerCount -eq 1
Add-Check -Name "Controlled server state" -Passed $serverStateHealthy -Detail $(
    if ($serverStateHealthy) {
        "The single controlled server is enabled, reachable, and online."
    }
    else {
        "The controlled server state differs from the release baseline."
    })

$latestPollAtUtc = ConvertTo-UtcTimestamp `
    -Value ([string]$database.latestPollAtUtc) `
    -Description "Latest poll time"
$latestPollAge = $collectedAtUtc - $latestPollAtUtc
$latestPollFresh = $latestPollAge -ge [TimeSpan]::Zero -and
    $latestPollAge -le [TimeSpan]::FromMinutes($PollFreshnessMinutes)
Add-Check -Name "Latest poll freshness" -Passed $latestPollFresh -Detail $(
    if ($latestPollFresh) {
        "The latest persisted poll is within the freshness bound."
    }
    else {
        "The latest persisted poll is stale or in the future."
    })

$expectedPolls = [Math]::Max(
    1,
    [Math]::Floor($elapsed.TotalSeconds / $pollIntervalSeconds))
$pollCoveragePercent = [Math]::Min(
    100,
    ([int]$database.pollTotal * 100.0) / $expectedPolls)
$pollCoverageReady = $pollCoveragePercent -ge $MinimumPollCoveragePercent
Add-Check -Name "Polling coverage" -Passed $pollCoverageReady -Detail $(
    if ($pollCoverageReady) {
        "Persisted polling coverage is $($pollCoveragePercent.ToString('N2')) percent."
    }
    else {
        "Persisted polling coverage is below $MinimumPollCoveragePercent percent."
    })

$pollOutcomesHealthy = [int]$database.pollTotal -gt 0 -and
    [int]$database.pollSuccessful -eq [int]$database.pollTotal -and
    [int]$database.pollFailed -eq 0 -and
    [int]$database.botPositivePolls -eq 0
Add-Check -Name "Polling outcomes" -Passed $pollOutcomesHealthy -Detail $(
    if ($pollOutcomesHealthy) {
        "All persisted soak polls succeeded with zero bots."
    }
    else {
        "The soak contains a failed poll, a bot-positive poll, or no poll data."
    })

$latencySamplesComplete = [int]$database.latencySampleCount -eq
    [int]$database.pollSuccessful
Add-Check -Name "Polling latency samples" -Passed $latencySamplesComplete -Detail $(
    if ($latencySamplesComplete) {
        "Every successful poll has a latency sample."
    }
    else {
        "The latency distribution does not cover every successful poll."
    })

$maximumAllowedGapSeconds = ($pollIntervalSeconds * 2) + 10
$maximumGapSeconds = if ($null -eq $database.maximumPollGapSeconds) {
    0.0
}
else {
    [double]$database.maximumPollGapSeconds
}
$pollGapHealthy = [int]$database.pollTotal -le 1 -or
    $maximumGapSeconds -le $maximumAllowedGapSeconds
Add-Check -Name "Polling continuity" -Passed $pollGapHealthy -Detail $(
    if ($pollGapHealthy) {
        "The maximum persisted poll gap is within the bounded tolerance."
    }
    else {
        "The maximum persisted poll gap exceeds the bounded tolerance."
    })

$incidentStateHealthy = [int]$database.incidentsOpened -eq 0 -and
    [int]$database.openIncidentCount -eq 0
Add-Check -Name "Incident state" -Passed $incidentStateHealthy -Detail $(
    if ($incidentStateHealthy) {
        "No availability incident opened during the soak and none remains open."
    }
    else {
        "An availability incident opened during the soak or remains open."
    })

$commandStateHealthy = [int]$database.commandsFailed -eq 0 -and
    [int]$database.incompleteCommandCount -eq 0
Add-Check -Name "Command outcomes" -Passed $commandStateHealthy -Detail $(
    if ($commandStateHealthy) {
        "No failed or incomplete command remains in the soak observation."
    }
    else {
        "A failed or incomplete command exists in the soak observation."
    })

$outboxStateHealthy = [int]$database.pendingOutboxCount -eq 0 -and
    [int]$database.deadLetterCount -eq 0
Add-Check -Name "Alert outbox state" -Passed $outboxStateHealthy -Detail $(
    if ($outboxStateHealthy) {
        "The alert outbox has no pending work or dead letters."
    }
    else {
        "The alert outbox contains pending work or dead letters."
    })

$scrapeIntervalSeconds = [int]$observation.Telemetry.ScrapeIntervalSeconds
Assert-Condition `
    -Condition ($scrapeIntervalSeconds -gt 0) `
    -Message "The observed scrape interval must be positive."
$expectedScrapeSamples = [Math]::Max(
    1,
    [Math]::Floor($elapsed.TotalSeconds / $scrapeIntervalSeconds))
$pipelineCoveragePercent = [Math]::Min(
    100,
    ([double]$observation.Telemetry.GoldSrcOpsSamples * 100.0) /
        $expectedScrapeSamples)
$pipelineHealthyPercent = [Math]::Min(
    100,
    ([double]$observation.Telemetry.GoldSrcOpsHealthySamples * 100.0) /
        $expectedScrapeSamples)
$collectorCoveragePercent = [Math]::Min(
    100,
    ([double]$observation.Telemetry.CollectorSamples * 100.0) /
        $expectedScrapeSamples)
$collectorHealthyPercent = [Math]::Min(
    100,
    ([double]$observation.Telemetry.CollectorHealthySamples * 100.0) /
        $expectedScrapeSamples)
$telemetryHealthy = [double]$observation.Telemetry.GoldSrcOpsUp -eq 1 -and
    [double]$observation.Telemetry.CollectorUp -eq 1 -and
    $pipelineCoveragePercent -ge $MinimumTelemetryCoveragePercent -and
    $pipelineHealthyPercent -ge $MinimumTelemetryCoveragePercent -and
    $collectorCoveragePercent -ge $MinimumTelemetryCoveragePercent -and
    $collectorHealthyPercent -ge $MinimumTelemetryCoveragePercent
Add-Check -Name "Telemetry coverage" -Passed $telemetryHealthy -Detail $(
    if ($telemetryHealthy) {
        "Collector and application-exporter scrape coverage meet the release threshold."
    }
    else {
        "Collector or application-exporter scrape coverage is below the release threshold."
    })

$freeDiskGiB = [Math]::Round([long]$observation.Host.FreeDiskBytes / 1GB, 2)
$availableMemoryMiB = [Math]::Round(
    [long]$observation.Host.AvailableMemoryBytes / 1MB,
    2)
$hostCapacityHealthy = $freeDiskGiB -ge $MinimumFreeDiskGiB -and
    $availableMemoryMiB -ge $MinimumAvailableMemoryMiB -and
    [double]$observation.Host.LoadAverageOneMinute -le
        ([int]$observation.Host.ProcessorCount * 2)
Add-Check -Name "Terminal host capacity" -Passed $hostCapacityHealthy -Detail $(
    if ($hostCapacityHealthy) {
        "Terminal disk, available memory, and one-minute load are within bounds."
    }
    else {
        "Terminal disk, memory, or load is outside the configured bound."
    })

$failedChecks = @($checks | Where-Object { -not $_.Passed })
$result = if ($failedChecks.Count -gt 0) {
    "Failed"
}
elseif ($durationComplete) {
    "Passed"
}
else {
    "InProgress"
}

$pollSuccessPercent = if ([int]$database.pollTotal -gt 0) {
    ([int]$database.pollSuccessful * 100.0) / [int]$database.pollTotal
}
else {
    0.0
}
$evidence = [ordered]@{
    SchemaVersion = 1
    Action = "V23SoakReadiness"
    CheckedAtUtc = $collectedAtUtc
    Source = $source
    TargetEvidence = $targetEvidence
    Result = $result
    Window = [ordered]@{
        StartedAtUtc = $startedAtUtc
        ExpectedCompletionAtUtc = $expectedCompletionAtUtc
        ObservedHours = [Math]::Round($elapsed.TotalHours, 4)
        RequiredHours = $ExpectedDurationHours
        Complete = $durationComplete
    }
    Candidate = [ordered]@{
        Version = [string]$observation.Candidate.Version
        SourceRevision = [string]$observation.Candidate.SourceRevision
        ApiImageDigest = [string]$observation.Candidate.ApiImageDigest
        MigrationRevision = [string]$observation.Candidate.MigrationRevision
    }
    Indicators = [ordered]@{
        HttpsEdge = [ordered]@{
            Measurement = "HostLocalCaddyHealth"
            CurrentLivenessHttpStatus = [int]$observation.Edge.LivenessHttpStatus
            CurrentReadinessHttpStatus = [int]$observation.Edge.ReadinessHttpStatus
        }
        Polling = [ordered]@{
            IntervalSeconds = $pollIntervalSeconds
            Expected = $expectedPolls
            Total = [int]$database.pollTotal
            Successful = [int]$database.pollSuccessful
            Failed = [int]$database.pollFailed
            SuccessPercent = [Math]::Round($pollSuccessPercent, 4)
            CoveragePercent = [Math]::Round($pollCoveragePercent, 4)
            BotPositivePolls = [int]$database.botPositivePolls
            LatencySampleCount = [int]$database.latencySampleCount
            AverageLatencyMs = $database.averageLatencyMs
            P95LatencyMs = $database.p95LatencyMs
            MaximumLatencyMs = $database.maximumLatencyMs
            MaximumGapSeconds = $database.maximumPollGapSeconds
        }
        Incidents = [ordered]@{
            Opened = [int]$database.incidentsOpened
            CurrentlyOpen = [int]$database.openIncidentCount
            MaximumDurationSeconds = $database.maximumIncidentDurationSeconds
        }
        Commands = [ordered]@{
            Total = [int]$database.commandsTotal
            Succeeded = [int]$database.commandsSucceeded
            Failed = [int]$database.commandsFailed
            CurrentlyIncomplete = [int]$database.incompleteCommandCount
        }
        AlertOutbox = [ordered]@{
            EventsCreated = [int]$database.outboxEventsTotal
            EventsProcessed = [int]$database.outboxEventsProcessed
            CurrentlyPending = [int]$database.pendingOutboxCount
            CurrentlyDeadLettered = [int]$database.deadLetterCount
        }
        Telemetry = [ordered]@{
            ScrapeIntervalSeconds = $scrapeIntervalSeconds
            ExpectedSamples = $expectedScrapeSamples
            ApplicationExporterCoveragePercent = [Math]::Round(
                $pipelineCoveragePercent,
                4)
            ApplicationExporterHealthyPercent = [Math]::Round(
                $pipelineHealthyPercent,
                4)
            CollectorCoveragePercent = [Math]::Round(
                $collectorCoveragePercent,
                4)
            CollectorHealthyPercent = [Math]::Round(
                $collectorHealthyPercent,
                4)
        }
        Capacity = [ordered]@{
            DatabaseSizeBytes = [long]$database.databaseSizeBytes
            PollSnapshotsTableSizeBytes = [long]$database.pollSnapshotsTableSizeBytes
            FreeDiskGiB = $freeDiskGiB
            AvailableMemoryMiB = $availableMemoryMiB
            ProcessorCount = [int]$observation.Host.ProcessorCount
            LoadAverageOneMinute = [double]$observation.Host.LoadAverageOneMinute
        }
    }
    Limitations = @(
        "This evidence samples HTTPS health through host-local Caddy and does not measure the external Internet path.",
        "External public API availability is sampled separately and has no continuous time series yet.",
        "Telemetry up measures the private scrape pipeline, not public API uptime.",
        "Host resource values are terminal observations rather than a continuous host time series.",
        "Game-service process continuity is recorded separately on the game-server host."
    )
    Checks = @($checks)
}

Write-SoakEvidence -Evidence $evidence -Path $EvidenceFile

foreach ($check in $checks) {
    $marker = if ($check.Passed) { "PASS" } else { "FAIL" }
    Write-Host "[$marker] $($check.Name): $($check.Detail)"
}

if ($failedChecks.Count -gt 0) {
    $failedNames = ($failedChecks | ForEach-Object { $_.Name }) -join ", "
    throw "Soak readiness failed: $failedNames."
}

if ($result -eq "InProgress") {
    Write-Host "Soak readiness checks passed; the required duration is still in progress."
}
else {
    Write-Host "Soak readiness passed."
}
