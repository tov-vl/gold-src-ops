#Requires -Version 7.0

<#
.SYNOPSIS
Restores an encrypted PostgreSQL backup into an isolated disposable database.

.DESCRIPTION
Streams one recoverable restic snapshot into pg_restore, applies the migration
bundle from the configured API image, validates the EF history and required
GoldSrcOps tables, records optional sanitized evidence, and removes all
decrypted disposable data before returning.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$EnvironmentFile,

    [string]$SnapshotId,

    [string]$EvidenceFile,

    [string]$LockFile,

    [ValidateRange(0, [int]::MaxValue)]
    [int]$ExpectedMinimumServerCount = 0,

    [ValidateRange(10, 300)]
    [int]$StartupTimeoutSeconds = 120,

    [string]$LocalRepositoryPath,

    [switch]$AllowLocalTestResources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "postgres-backup-common.ps1")

if ([string]::IsNullOrWhiteSpace($LockFile)) {
    $LockFile = Get-DefaultPostgresBackupLockFile
}

function Wait-RehearsalPostgres {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $state = Invoke-NativeCapture `
            -FilePath "docker" `
            -Arguments @("inspect", "--format", "{{.State.Status}}", $ContainerName) `
            -AllowFailure

        if ($state.ExitCode -eq 0 -and $state.Output -eq "running") {
            $ready = Invoke-NativeCapture `
                -FilePath "docker" `
                -Arguments @(
                    "exec", $ContainerName,
                    "pg_isready",
                    "--host=/var/run/postgresql",
                    "--username=goldsrcops",
                    "--dbname=goldsrcops") `
                -AllowFailure
            if ($ready.ExitCode -eq 0) {
                return
            }
        }
        elseif ($state.ExitCode -eq 0 -and $state.Output -in @("dead", "exited")) {
            throw "Disposable PostgreSQL stopped before it became ready."
        }

        Start-Sleep -Seconds 1
    }

    throw "Disposable PostgreSQL did not become ready within $TimeoutSeconds seconds."
}

function Invoke-RehearsalSql {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$Sql
    )

    $result = Invoke-NativeCapture -FilePath "docker" -Arguments @(
        "exec", $ContainerName,
        "psql",
        "--host=/var/run/postgresql",
        "--username=goldsrcops",
        "--dbname=goldsrcops",
        "--no-align",
        "--tuples-only",
        "--set", "ON_ERROR_STOP=1",
        "--command", $Sql)

    return $result.Output.Trim()
}

$configuration = Get-PostgresBackupConfiguration `
    -EnvironmentFile $EnvironmentFile `
    -LocalRepositoryPath $LocalRepositoryPath `
    -AllowLocalTestResources:$AllowLocalTestResources
$postgresImage = Get-RequiredDeploymentValue `
    -Values $configuration.Values `
    -Name "GOLDSRCOPS_POSTGRES_IMAGE"
$apiImage = Get-RequiredDeploymentValue `
    -Values $configuration.Values `
    -Name "GOLDSRCOPS_IMAGE"
Assert-ImmutableBackupImage `
    -Image $postgresImage `
    -Name "PostgreSQL" `
    -AllowLocalTestResources:$AllowLocalTestResources
Assert-ImmutableBackupImage `
    -Image $apiImage `
    -Name "API" `
    -AllowLocalTestResources:$AllowLocalTestResources

$lock = Enter-PostgresBackupLock -Path $LockFile
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$postgresContainer = "goldsrcops-restore-postgres-$runId"
$migrationContainer = "goldsrcops-restore-migration-$runId"
$resticContainer = "goldsrcops-restore-restic-$runId"
$dataVolume = "goldsrcops-restore-data-$runId"
$socketVolume = "goldsrcops-restore-socket-$runId"
$postgresCreated = $false
$dataVolumeCreated = $false
$socketVolumeCreated = $false
$succeeded = $false

try {
    $snapshot = Get-RecoverableBackupSnapshot `
        -Configuration $configuration `
        -SnapshotId $SnapshotId

    Invoke-NativeCapture -FilePath "docker" -Arguments @("volume", "create", $dataVolume) | Out-Null
    $dataVolumeCreated = $true
    Invoke-NativeCapture -FilePath "docker" -Arguments @("volume", "create", $socketVolume) | Out-Null
    $socketVolumeCreated = $true

    Invoke-NativeCapture -FilePath "docker" -Arguments @(
        "run", "--detach",
        "--name", $postgresContainer,
        "--network", "none",
        "--env", "POSTGRES_DB=goldsrcops",
        "--env", "POSTGRES_USER=goldsrcops",
        "--env", "POSTGRES_HOST_AUTH_METHOD=trust",
        "--env", "POSTGRES_INITDB_ARGS=--auth-local=trust",
        "--mount", "type=volume,source=$dataVolume,target=/var/lib/postgresql/data",
        "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
        $postgresImage) | Out-Null
    $postgresCreated = $true
    Wait-RehearsalPostgres `
        -ContainerName $postgresContainer `
        -TimeoutSeconds $StartupTimeoutSeconds

    $producerArguments = New-ResticDockerArguments `
        -Configuration $configuration `
        -ContainerName $resticContainer `
        -ResticArguments @(
            "--retry-lock", "5m",
            "dump",
            [string]$snapshot.id,
            "/$script:PostgresBackupArchiveName")
    $consumerArguments = @(
        "exec", "--interactive",
        $postgresContainer,
        "pg_restore",
        "--host=/var/run/postgresql",
        "--username=goldsrcops",
        "--dbname=goldsrcops",
        "--exit-on-error",
        "--single-transaction",
        "--no-owner",
        "--no-privileges")
    $restoreResult = Invoke-NativeStreamPipeline `
        -ProducerFilePath "docker" `
        -ProducerArguments $producerArguments `
        -ConsumerFilePath "docker" `
        -ConsumerArguments $consumerArguments

    if ($restoreResult.ProducerExitCode -ne 0 -or
        $restoreResult.ConsumerExitCode -ne 0 -or
        -not [string]::IsNullOrWhiteSpace($restoreResult.StreamError)) {
        $producerDetail = if ([string]::IsNullOrWhiteSpace($restoreResult.ProducerError)) {
            "no restic diagnostic"
        }
        else {
            $restoreResult.ProducerError
        }
        $consumerDetail = if ([string]::IsNullOrWhiteSpace($restoreResult.ConsumerError)) {
            "no pg_restore diagnostic"
        }
        else {
            $restoreResult.ConsumerError
        }
        $streamDetail = if ([string]::IsNullOrWhiteSpace($restoreResult.StreamError)) {
            "no stream diagnostic"
        }
        else {
            $restoreResult.StreamError
        }

        throw "PostgreSQL restore pipeline failed. restic: $producerDetail; pg_restore: $consumerDetail; stream: $streamDetail"
    }

    $rehearsalConnection = "Host=/var/run/postgresql;Port=5432;Database=goldsrcops;Username=goldsrcops;SSL Mode=Disable;Timeout=5;Command Timeout=30"
    Invoke-NativeCapture -FilePath "docker" -Arguments @(
        "run", "--rm",
        "--name", $migrationContainer,
        "--network", "none",
        "--read-only",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m,mode=0700,uid=1654,gid=1654",
        "--tmpfs", "/run/secrets:rw,noexec,nosuid,size=1m,mode=0700,uid=1654,gid=1654",
        "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
        "--env", "DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net",
        "--env", "REHEARSAL_CONNECTION=$rehearsalConnection",
        "--entrypoint", "/bin/sh",
        $apiImage,
        "-ec",
        'umask 077; printf "%s" "$REHEARSAL_CONNECTION" > /run/secrets/database-connection; exec /app/api-entrypoint.sh migrate --no-color --prefix-output') | Out-Null

    $expectedTables = @(
        "availability_incidents",
        "command_executions",
        "outbox_messages",
        "outbox_replay_requests",
        "poll_snapshots",
        "server_credentials",
        "server_current_states",
        "servers"
    )
    $tableOutput = Invoke-RehearsalSql `
        -ContainerName $postgresContainer `
        -Sql "SELECT table_name FROM information_schema.tables WHERE table_schema = 'goldsrcops' ORDER BY table_name;"
    $actualTables = @(
        $tableOutput -split '\r?\n' |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    $missingTables = @($expectedTables | Where-Object { $_ -notin $actualTables })
    Assert-BackupCondition `
        -Condition ($missingTables.Count -eq 0) `
        -Message "Restored database is missing required GoldSrcOps tables."

    $migrationCount = [int](Invoke-RehearsalSql `
            -ContainerName $postgresContainer `
            -Sql 'SELECT COUNT(*) FROM public."__EFMigrationsHistory";')
    $serverCount = [int](Invoke-RehearsalSql `
            -ContainerName $postgresContainer `
            -Sql 'SELECT COUNT(*) FROM goldsrcops.servers;')
    $databaseSizeBytes = [long](Invoke-RehearsalSql `
            -ContainerName $postgresContainer `
            -Sql "SELECT pg_database_size('goldsrcops');")

    Assert-BackupCondition `
        -Condition ($migrationCount -gt 0) `
        -Message "Restored database has no EF Core migration history."
    Assert-BackupCondition `
        -Condition ($serverCount -ge $ExpectedMinimumServerCount) `
        -Message "Restored server count is below the required rehearsal minimum."

    Write-BackupEvidence -Path $EvidenceFile -Evidence @{
        Action = "PostgreSQLRestoreRehearsal"
        ApiImage = $apiImage
        CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        DatabaseSizeBytes = $databaseSizeBytes
        MigrationCount = $migrationCount
        PostgresImage = $postgresImage
        RequiredTables = $expectedTables
        ResticImage = $configuration.ResticImage
        ServerCount = $serverCount
        SnapshotId = [string]$snapshot.id
        SnapshotTime = [string]$snapshot.time
    }

    $succeeded = $true
    Write-Host "Encrypted PostgreSQL backup restore rehearsal passed."
    Write-Host "Snapshot: $($snapshot.id)"
    Write-Host "Migrations: $migrationCount; servers: $serverCount; database bytes: $databaseSizeBytes"
}
finally {
    foreach ($container in @($migrationContainer, $resticContainer)) {
        $cleanup = Invoke-NativeCapture `
            -FilePath "docker" `
            -Arguments @("rm", "--force", $container) `
            -AllowFailure
        if ($cleanup.ExitCode -ne 0 -and
            $cleanup.Error -notmatch '(?i)no such container') {
            Write-Warning "Could not remove disposable container '$container'."
        }
    }

    if ($postgresCreated) {
        $cleanup = Invoke-NativeCapture `
            -FilePath "docker" `
            -Arguments @("rm", "--force", $postgresContainer) `
            -AllowFailure
        if ($cleanup.ExitCode -ne 0) {
            Write-Warning "Could not remove disposable PostgreSQL container '$postgresContainer'."
        }
    }

    foreach ($volume in @(
            @{ Name = $dataVolume; Created = $dataVolumeCreated },
            @{ Name = $socketVolume; Created = $socketVolumeCreated })) {
        if ($volume.Created) {
            $cleanup = Invoke-NativeCapture `
                -FilePath "docker" `
                -Arguments @("volume", "rm", "--force", $volume.Name) `
                -AllowFailure
            if ($cleanup.ExitCode -ne 0) {
                Write-Warning "Could not remove disposable volume '$($volume.Name)'."
            }
        }
    }

    $lock.Dispose()

    if (-not $succeeded) {
        Write-Warning "Restore rehearsal failed; disposable recovery resources were scheduled for removal."
    }
}
