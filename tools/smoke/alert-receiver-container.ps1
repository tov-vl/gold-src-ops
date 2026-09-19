#Requires -Version 7.0

<#
.SYNOPSIS
Builds and smoke-tests the production AlertReceiver image and recovery path.

.DESCRIPTION
Creates isolated Docker resources, validates the tracked deployment contract,
applies both receiver migrations twice, starts the receiver in CatchUp mode with
provider delivery disabled, verifies durable idempotent ingestion, creates an
encrypted local restic snapshot, and restores that snapshot into a disposable
database before cleaning every temporary resource.

.PARAMETER Image
Pulls and tests an existing AlertReceiver image by immutable sha256 digest.

.PARAMETER ExpectedImageSource
Expected org.opencontainers.image.source label for an existing image.

.PARAMETER ExpectedImageRevision
Expected org.opencontainers.image.revision label for an existing image.

.PARAMETER ExpectedImageVersion
Expected org.opencontainers.image.version label for an existing image.
#>

[CmdletBinding()]
param(
    [ValidatePattern('^ghcr\.io/[a-z0-9][a-z0-9._/-]*@sha256:[a-f0-9]{64}$')]
    [string]$Image,

    [string]$ExpectedImageSource,

    [string]$ExpectedImageRevision,

    [string]$ExpectedImageVersion,

    [ValidateRange(10, 300)]
    [int]$StartupTimeoutSeconds = 90,

    [switch]$KeepImage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$buildImageLocally = [string]::IsNullOrWhiteSpace($Image)
$imageTag = if ($buildImageLocally) { "goldsrcops-alert-receiver:smoke-$runId" } else { $Image }
$postgresImage = "postgres:16-alpine"
$resticImage = "restic/restic:0.19.1"
$postgresContainer = "goldsrcops-receiver-smoke-postgres-$runId"
$receiverContainer = "goldsrcops-receiver-smoke-runtime-$runId"
$migrationContainer = "goldsrcops-receiver-smoke-migration-$runId"
$dataVolume = "goldsrcops-receiver-smoke-data-$runId"
$socketVolume = "goldsrcops-receiver-smoke-socket-$runId"
$databaseName = "goldsrcops_receiver"
$databaseUser = "goldsrcops_receiver"
$databasePassword = "goldsrcops-receiver-smoke-$runId"
$receiverAuthorization = "Bearer receiver-smoke-$runId"
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-receiver-smoke-$runId"
$databaseConnectionFile = Join-Path $temporaryDirectory "database-connection"
$receiverAuthorizationFile = Join-Path $temporaryDirectory "receiver-authorization"
$postgresPasswordFile = Join-Path $temporaryDirectory "postgres-password"
$resticPasswordFile = Join-Path $temporaryDirectory "restic-password"
$resticEnvironmentFile = Join-Path $temporaryDirectory "restic-environment"
$backupEnvironmentFile = Join-Path $temporaryDirectory "deployment.env"
$backupEvidenceFile = Join-Path $temporaryDirectory "backup-evidence.json"
$restoreEvidenceFile = Join-Path $temporaryDirectory "restore-evidence.json"
$resticRepositoryDirectory = Join-Path $temporaryDirectory "repository"
$imageBuilt = $false
$postgresCreated = $false
$dataVolumeCreated = $false
$socketVolumeCreated = $false
$succeeded = $false

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Name)

    Write-Host ""
    Write-Host "==> $Name"
}

function Set-OwnerOnlyFilePermissions {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Path
    )

    if ($IsWindows) {
        return
    }

    $mode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
    foreach ($item in $Path) {
        [IO.File]::SetUnixFileMode($item, $mode)
    }
}

function Set-ContainerReadableFilePermissions {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Path
    )

    if ($IsWindows) {
        return
    }

    $mode =
        [IO.UnixFileMode]::UserRead -bor
        [IO.UnixFileMode]::UserWrite -bor
        [IO.UnixFileMode]::GroupRead -bor
        [IO.UnixFileMode]::OtherRead
    foreach ($item in $Path) {
        [IO.File]::SetUnixFileMode($item, $mode)
    }
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-ExternalCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = @(& $FilePath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $text = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "Command '$FilePath' failed with exit code $exitCode. $text"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $text
    }
}

function Wait-Postgres {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $result = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "exec", $postgresContainer,
            "/bin/sh", "-ec",
            'export PGPASSWORD="$(cat /run/secrets/receiver-postgres-password)"; exec pg_isready --host=/var/run/postgresql --username="$POSTGRES_USER" --dbname="$POSTGRES_DB"') -AllowFailure
        if ($result.ExitCode -eq 0) {
            return
        }

        Start-Sleep -Seconds 1
    }

    throw "AlertReceiver PostgreSQL did not become ready in time."
}

function Invoke-ReceiverSql {
    param([Parameter(Mandatory = $true)][string]$Sql)

    $result = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "exec", "--env", "SMOKE_SQL=$Sql", $postgresContainer,
        "/bin/sh", "-ec",
        'export PGPASSWORD="$(cat /run/secrets/receiver-postgres-password)"; exec psql --host=/var/run/postgresql --username="$POSTGRES_USER" --dbname="$POSTGRES_DB" --no-align --tuples-only --set ON_ERROR_STOP=1 --command "$SMOKE_SQL"')
    return $result.Output.Trim()
}

function Wait-ReceiverHealth {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $lastProbeOutput = "No readiness probe completed."
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $probeScript = @'
exec 3<>/dev/tcp/127.0.0.1/8080
printf 'GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3
read -r status <&3
[[ "$status" == *" 200 "* ]]
'@
        $probeScript = $probeScript.Replace("`r", "")
        $result = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "exec", $receiverContainer,
            "bash", "-c", $probeScript) -AllowFailure
        if (-not [string]::IsNullOrWhiteSpace($result.Output)) {
            $lastProbeOutput = $result.Output
        }
        if ($result.ExitCode -eq 0) {
            return
        }

        $state = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "inspect", "--format", "{{.State.Status}}", $receiverContainer) -AllowFailure
        if ($state.ExitCode -eq 0 -and $state.Output -in @("dead", "exited")) {
            throw "AlertReceiver stopped before readiness passed."
        }

        Start-Sleep -Seconds 1
    }

    $containerHealth = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "inspect", "--format", "{{json .State.Health}}", $receiverContainer) -AllowFailure
    throw "AlertReceiver readiness did not pass in time. Last probe: $lastProbeOutput Container health: $($containerHealth.Output)"
}

function Invoke-AvailabilityEvent {
    param(
        [Parameter(Mandatory = $true)][string]$Body,
        [Parameter(Mandatory = $true)][string]$EventId
    )

    $script = @'
set -eu
authorization="$(cat /run/secrets/receiver-authorization)"
content_length="${#SMOKE_BODY}"
exec 3<>/dev/tcp/127.0.0.1/8080
printf 'POST /api/v1/availability-events HTTP/1.1\r\nHost: localhost\r\nAuthorization: %s\r\nIdempotency-Key: %s\r\nContent-Type: application/json\r\nContent-Length: %s\r\nConnection: close\r\n\r\n%s' "$authorization" "$SMOKE_EVENT_ID" "$content_length" "$SMOKE_BODY" >&3
IFS= read -r status <&3
printf '%s' "$status"
'@
    $script = $script.Replace("`r", "")
    $result = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "exec",
        "--env", "SMOKE_BODY=$Body",
        "--env", "SMOKE_EVENT_ID=$EventId",
        $receiverContainer,
        "bash", "-c", $script)
    return $result.Output.Trim()
}

function Remove-ContainerIfPresent {
    param([Parameter(Mandatory = $true)][string]$Name)

    [void](Invoke-ExternalCapture -FilePath "docker" -Arguments @("rm", "--force", $Name) -AllowFailure)
}

function Remove-VolumeIfPresent {
    param([Parameter(Mandatory = $true)][string]$Name)

    [void](Invoke-ExternalCapture -FilePath "docker" -Arguments @("volume", "rm", "--force", $Name) -AllowFailure)
}

Push-Location $repoRoot
try {
    Write-Step "Validate tracked AlertReceiver deployment contract"
    & ./ops/alert-receiver/preflight.ps1 `
        -EnvironmentFile ./ops/alert-receiver/deployment.env.example `
        -ContractOnly
    & ./ops/alert-receiver/muted-provider-preflight.ps1 `
        -EnvironmentFile ./ops/alert-receiver/deployment.env.example `
        -ContractOnly

    if ($buildImageLocally) {
        Write-Step "Build production AlertReceiver image"
        $localImageSource = "https://github.com/tov-vl/gold-src-ops"
        $localImageRevision = (Invoke-ExternalCapture -FilePath "git" -Arguments @(
                "rev-parse",
                "HEAD")).Output
        $localImageVersion = "smoke-$runId"

        Invoke-External -FilePath "docker" -Arguments @(
            "build",
            "--file", "Dockerfile.receiver",
            "--label", "org.opencontainers.image.source=$localImageSource",
            "--label", "org.opencontainers.image.revision=$localImageRevision",
            "--label", "org.opencontainers.image.version=$localImageVersion",
            "--label", "org.opencontainers.image.licenses=MIT",
            "--tag", $imageTag,
            ".")
        $imageBuilt = $true
        $ExpectedImageSource = $localImageSource
        $ExpectedImageRevision = $localImageRevision
        $ExpectedImageVersion = $localImageVersion
    }
    else {
        Write-Step "Pull production AlertReceiver image by digest"
        Invoke-External -FilePath "docker" -Arguments @("pull", $imageTag)

        $repoDigests = @(
            (Invoke-ExternalCapture -FilePath "docker" -Arguments @(
                    "image",
                    "inspect",
                    "--format",
                    "{{json .RepoDigests}}",
                    $imageTag)).Output |
                ConvertFrom-Json)
        $expectedDigest = ($Image -split "@", 2)[1]
        if ($null -eq ($repoDigests | Where-Object {
                    $_.EndsWith("@$expectedDigest", [StringComparison]::Ordinal)
                })) {
            throw "Docker did not retain the requested AlertReceiver image digest after pull."
        }
    }

    Write-Step "Verify AlertReceiver runtime image"
    $runtimeUser = (Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "image", "inspect", "--format", "{{.Config.User}}", $imageTag)).Output
    if ($runtimeUser -ne "1654") {
        throw "AlertReceiver image must run as Unix UID 1654."
    }

    $labels = (Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "image",
            "inspect",
            "--format",
            "{{json .Config.Labels}}",
            $imageTag)).Output | ConvertFrom-Json -AsHashtable
    $expectedLabels = @{
        "org.opencontainers.image.source" = $ExpectedImageSource
        "org.opencontainers.image.revision" = $ExpectedImageRevision
        "org.opencontainers.image.version" = $ExpectedImageVersion
        "org.opencontainers.image.licenses" = "MIT"
    }
    foreach ($entry in $expectedLabels.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$labels[$entry.Key]) -or
            (-not [string]::IsNullOrWhiteSpace([string]$entry.Value) -and
             [string]$labels[$entry.Key] -cne [string]$entry.Value)) {
            throw "OCI label '$($entry.Key)' does not match the AlertReceiver image contract."
        }
    }

    if ([string]$labels["org.opencontainers.image.revision"] -notmatch '\A[0-9a-f]{40}\z') {
        throw "OCI revision label must contain a full Git commit SHA."
    }

    Invoke-External -FilePath "docker" -Arguments @(
        "run", "--rm", "--network", "none", "--read-only",
        "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
        "--entrypoint", "/bin/sh", $imageTag, "-ec",
        "test -x /app/goldsrcops-alert-receiver-migrate && test -x /app/receiver-entrypoint.sh && test ! -e /app/appsettings.Development.json")

    $failFast = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "run", "--rm", "--network", "none", $imageTag) -AllowFailure
    if ($failFast.ExitCode -eq 0 -or
        $failFast.Output -notmatch "Required receiver database connection secret is missing or empty") {
        throw "AlertReceiver image did not fail closed without its database secret."
    }

    Write-Step "Start isolated receiver PostgreSQL"
    New-Item -ItemType Directory -Path $temporaryDirectory, $resticRepositoryDirectory -Force | Out-Null
    [IO.File]::WriteAllText($postgresPasswordFile, $databasePassword)
    [IO.File]::WriteAllText(
        $databaseConnectionFile,
        "Host=/var/run/postgresql;Port=5432;Database=$databaseName;Username=$databaseUser;Password=$databasePassword;SSL Mode=Disable;Timeout=5;Command Timeout=30")
    [IO.File]::WriteAllText($receiverAuthorizationFile, $receiverAuthorization)
    [IO.File]::WriteAllText($resticPasswordFile, "restic-$runId")
    [IO.File]::WriteAllText($resticEnvironmentFile, "# Local isolated restic backend.`n")
    Set-ContainerReadableFilePermissions -Path @(
        $postgresPasswordFile,
        $databaseConnectionFile,
        $receiverAuthorizationFile)
    Set-OwnerOnlyFilePermissions -Path @(
        $resticPasswordFile,
        $resticEnvironmentFile)

    $providerFailFast = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "run", "--rm", "--network", "none",
        "--mount", "type=bind,source=$databaseConnectionFile,target=/run/secrets/receiver-database-connection,readonly",
        "--mount", "type=bind,source=$receiverAuthorizationFile,target=/run/secrets/receiver-authorization,readonly",
        "--env", "ProviderDelivery__Enabled=true",
        $imageTag) -AllowFailure
    if ($providerFailFast.ExitCode -eq 0 -or
        $providerFailFast.Output -notmatch "Required provider endpoint secret is missing or empty") {
        throw "AlertReceiver image did not fail closed without its provider endpoint secret."
    }

    Invoke-External -FilePath "docker" -Arguments @("volume", "create", $dataVolume)
    $dataVolumeCreated = $true
    Invoke-External -FilePath "docker" -Arguments @("volume", "create", $socketVolume)
    $socketVolumeCreated = $true
    Invoke-External -FilePath "docker" -Arguments @(
        "run", "--detach",
        "--name", $postgresContainer,
        "--network", "none",
        "--env", "PGDATA=/var/lib/postgresql/data/pgdata",
        "--env", "POSTGRES_DB=$databaseName",
        "--env", "POSTGRES_USER=$databaseUser",
        "--env", "POSTGRES_PASSWORD_FILE=/run/secrets/receiver-postgres-password",
        "--env", "POSTGRES_INITDB_ARGS=--auth-local=scram-sha-256 --auth-host=scram-sha-256",
        "--mount", "type=bind,source=$postgresPasswordFile,target=/run/secrets/receiver-postgres-password,readonly",
        "--mount", "type=volume,source=$dataVolume,target=/var/lib/postgresql/data",
        "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
        $postgresImage)
    $postgresCreated = $true
    Wait-Postgres

    Write-Step "Apply and reapply both receiver migrations"
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Invoke-External -FilePath "docker" -Arguments @(
            "run", "--rm",
            "--name", "$migrationContainer-$attempt",
            "--network", "none",
            "--read-only",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m,mode=0700,uid=1654,gid=1654",
            "--mount", "type=bind,source=$databaseConnectionFile,target=/run/secrets/receiver-database-connection,readonly",
            "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
            "--env", "DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net",
            $imageTag,
            "migrate", "--no-color", "--prefix-output")
    }

    $migrationCount = [int](Invoke-ReceiverSql -Sql 'SELECT COUNT(*) FROM receiver."__EFMigrationsHistory";')
    if ($migrationCount -ne 2) {
        throw "AlertReceiver migration history contains $migrationCount rows instead of 2."
    }

    Write-Step "Start CatchUp receiver with provider delivery disabled"
    Invoke-External -FilePath "docker" -Arguments @(
        "run", "--detach",
        "--name", $receiverContainer,
        "--network", "none",
        "--read-only",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
        "--mount", "type=bind,source=$databaseConnectionFile,target=/run/secrets/receiver-database-connection,readonly",
        "--mount", "type=bind,source=$receiverAuthorizationFile,target=/run/secrets/receiver-authorization,readonly",
        "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
        "--env", "ASPNETCORE_ENVIRONMENT=Production",
        "--env", "AllowedHosts=localhost",
        "--env", "Receiver__Mode=CatchUp",
        "--env", "ProviderDelivery__Enabled=false",
        $imageTag)
    Wait-ReceiverHealth

    $eventId = [Guid]::NewGuid()
    $incidentId = [Guid]::NewGuid()
    $serverId = [Guid]::NewGuid()
    $openingTime = [DateTimeOffset]::FromUnixTimeSeconds(
        [DateTimeOffset]::UtcNow.AddMinutes(-1).ToUnixTimeSeconds()).AddTicks(1234567)
    $occurredAt = $openingTime.ToString("O")
    $event = [ordered]@{
        payloadVersion = 1
        eventId = $eventId
        incidentId = $incidentId
        serverId = $serverId
        eventType = "server.availability.unavailable"
        serverName = "Container smoke"
        occurredAtUtc = $occurredAt
        openedAtUtc = $occurredAt
        closedAtUtc = $null
        reason = "Synthetic receiver container smoke."
        consecutiveFailures = 1
        durationSeconds = $null
    }
    $body = $event | ConvertTo-Json -Compress

    $firstStatus = Invoke-AvailabilityEvent -Body $body -EventId $eventId
    $duplicateStatus = Invoke-AvailabilityEvent -Body $body -EventId $eventId
    if ($firstStatus -notmatch '^HTTP/1\.1 202 ' -or $duplicateStatus -notmatch '^HTTP/1\.1 204 ') {
        throw "AlertReceiver container did not preserve 202/204 durable idempotency."
    }
    Invoke-External -FilePath "docker" -Arguments @("restart", $receiverContainer)
    Wait-ReceiverHealth
    $event.eventId = [Guid]::NewGuid()
    $event.eventType = "server.availability.recovered"
    $event.closedAtUtc = $openingTime.AddSeconds(30).ToString("O")
    $event.occurredAtUtc = $event.closedAtUtc
    $event.durationSeconds = 30
    $event.reason = "Synthetic receiver recovery."
    $recoveryBody = $event | ConvertTo-Json -Compress
    $recoveryStatus = Invoke-AvailabilityEvent -Body $recoveryBody -EventId $event.eventId
    $duplicateRecoveryStatus = Invoke-AvailabilityEvent -Body $recoveryBody -EventId $event.eventId
    if ($recoveryStatus -notmatch '^HTTP/1\.1 202 ' -or $duplicateRecoveryStatus -notmatch '^HTTP/1\.1 204 ') {
        throw "AlertReceiver did not recover the submicrosecond incident idempotently after restart."
    }
    $eventCount = [int](Invoke-ReceiverSql -Sql 'SELECT COUNT(*) FROM receiver.events;')
    $providerOutboxCount = [int](Invoke-ReceiverSql -Sql 'SELECT COUNT(*) FROM receiver.provider_outbox_messages;')
    if ($eventCount -ne 2 -or $providerOutboxCount -ne 0) {
        throw "CatchUp container smoke did not preserve the suppressed durable pair."
    }

    Write-Step "Create encrypted receiver backup and run isolated restore rehearsal"
    $environmentLines = @(
        "GOLDSRCOPS_ALERT_RECEIVER_IMAGE=$imageTag",
        "GOLDSRCOPS_ALERT_RECEIVER_POSTGRES_IMAGE=$postgresImage",
        "GOLDSRCOPS_ALERT_RECEIVER_HOSTNAME=receiver-smoke",
        "GOLDSRCOPS_RESTIC_IMAGE=$resticImage",
        "GOLDSRCOPS_BACKUP_HOST=receiver-smoke",
        "GOLDSRCOPS_BACKUP_REPOSITORY=/repository",
        "GOLDSRCOPS_RESTIC_PASSWORD_FILE=$resticPasswordFile",
        "GOLDSRCOPS_RESTIC_ENVIRONMENT_FILE=$resticEnvironmentFile"
    )
    [IO.File]::WriteAllLines($backupEnvironmentFile, $environmentLines)

    & ./ops/production/postgres-backup.ps1 `
        -Action Initialize `
        -EnvironmentFile $backupEnvironmentFile `
        -Workload AlertReceiver `
        -LocalRepositoryPath $resticRepositoryDirectory `
        -AllowLocalTestResources
    & ./ops/production/postgres-backup.ps1 `
        -Action Create `
        -EnvironmentFile $backupEnvironmentFile `
        -Workload AlertReceiver `
        -SourceContainer $postgresContainer `
        -LocalRepositoryPath $resticRepositoryDirectory `
        -EvidenceFile $backupEvidenceFile `
        -AllowLocalTestResources
    & ./ops/production/postgres-backup.ps1 `
        -Action Check `
        -EnvironmentFile $backupEnvironmentFile `
        -Workload AlertReceiver `
        -ReadDataSubset 100% `
        -LocalRepositoryPath $resticRepositoryDirectory `
        -AllowLocalTestResources
    & ./ops/production/postgres-restore-rehearsal.ps1 `
        -EnvironmentFile $backupEnvironmentFile `
        -Workload AlertReceiver `
        -ExpectedMinimumReceiverEventCount 2 `
        -ReapplyMigration `
        -LocalRepositoryPath $resticRepositoryDirectory `
        -EvidenceFile $restoreEvidenceFile `
        -AllowLocalTestResources

    $backupEvidence = Get-Content -LiteralPath $backupEvidenceFile -Raw | ConvertFrom-Json
    $restoreEvidence = Get-Content -LiteralPath $restoreEvidenceFile -Raw | ConvertFrom-Json
    if ($backupEvidence.Workload -ne "AlertReceiver" -or
        $restoreEvidence.Workload -ne "AlertReceiver" -or
        $backupEvidence.SnapshotId -ne $restoreEvidence.SnapshotId -or
        [int]$restoreEvidence.MigrationCount -ne 2 -or
        [int]$restoreEvidence.ReceiverEventCount -ne 2 -or
        -not [bool]$restoreEvidence.MigrationReapplicationVerified) {
        throw "AlertReceiver recovery evidence did not prove the expected snapshot, migrations, and event ledger."
    }

    $succeeded = $true
    Write-Step "AlertReceiver container and restore smoke passed"
}
finally {
    if (-not $succeeded) {
        foreach ($container in @($receiverContainer, $postgresContainer)) {
            $exists = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
                "container", "inspect", $container) -AllowFailure
            if ($exists.ExitCode -eq 0) {
                Write-Host ""
                Write-Host "Container logs for $container`:"
                & docker logs --tail 100 $container 2>&1 | ForEach-Object { Write-Host $_ }
            }
        }
    }

    Remove-ContainerIfPresent -Name $receiverContainer
    if ($postgresCreated) {
        Remove-ContainerIfPresent -Name $postgresContainer
    }
    if ($dataVolumeCreated) {
        Remove-VolumeIfPresent -Name $dataVolume
    }
    if ($socketVolumeCreated) {
        Remove-VolumeIfPresent -Name $socketVolume
    }

    if (Test-Path -LiteralPath $temporaryDirectory) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
        if (-not $resolvedTemporaryDirectory.StartsWith(
                $temporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an AlertReceiver smoke directory outside the temporary root."
        }
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
    }

    if ($imageBuilt -and -not $KeepImage) {
        [void](Invoke-ExternalCapture -FilePath "docker" -Arguments @(
                "image", "rm", "--force", $imageTag) -AllowFailure)
    }
    elseif ($imageBuilt) {
        Write-Host "Kept AlertReceiver smoke image: $imageTag"
    }

    Pop-Location
}
