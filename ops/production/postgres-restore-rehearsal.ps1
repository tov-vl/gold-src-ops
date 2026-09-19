#Requires -Version 7.0

<#
.SYNOPSIS
Restores an encrypted PostgreSQL backup into an isolated disposable database.

.DESCRIPTION
Streams one workload-scoped recoverable restic snapshot into pg_restore,
applies the configured runtime image's migration bundle, optionally reapplies
that bundle, validates the workload-specific EF history and tables, records
optional sanitized evidence, and removes all decrypted disposable data before
returning. The ControlPlane profile can additionally start a previous API image
with a read-only database connection.
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

    [ValidateRange(0, [int]::MaxValue)]
    [int]$ExpectedMinimumReceiverEventCount = 0,

    [switch]$ReapplyMigration,

    [string]$PreviousApiImage,

    [ValidateRange(10, 300)]
    [int]$StartupTimeoutSeconds = 120,

    [string]$LocalRepositoryPath,

    [ValidateSet("ControlPlane", "AlertReceiver")]
    [string]$Workload = "ControlPlane",

    [switch]$AllowLocalTestResources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "postgres-backup-common.ps1")
Set-PostgresBackupWorkload -Workload $Workload

if ([string]::IsNullOrWhiteSpace($LockFile)) {
    $LockFile = Get-DefaultPostgresBackupLockFile
}

function Wait-RehearsalPostgres {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$DatabaseName,

        [Parameter(Mandatory = $true)]
        [string]$DatabaseUser,

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
                    "--username=$DatabaseUser",
                    "--dbname=$DatabaseName") `
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
        [string]$DatabaseName,

        [Parameter(Mandatory = $true)]
        [string]$DatabaseUser,

        [Parameter(Mandatory = $true)]
        [string]$Sql
    )

    $result = Invoke-NativeCapture -FilePath "docker" -Arguments @(
        "exec", $ContainerName,
        "psql",
        "--host=/var/run/postgresql",
        "--username=$DatabaseUser",
        "--dbname=$DatabaseName",
        "--no-align",
        "--tuples-only",
        "--set", "ON_ERROR_STOP=1",
        "--command", $Sql)

    return $result.Output.Trim()
}

function Invoke-RehearsalMigration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$SocketVolume,

        [Parameter(Mandatory = $true)]
        [string]$RuntimeImage,

        [Parameter(Mandatory = $true)]
        [string]$EntrypointPath,

        [Parameter(Mandatory = $true)]
        [string]$DatabaseSecretName,

        [Parameter(Mandatory = $true)]
        [string]$ConnectionString
    )

    $migrationCommand = 'umask 077; printf "%s" "$REHEARSAL_CONNECTION" > /run/secrets/{0}; exec {1} migrate --no-color --prefix-output' -f `
        $DatabaseSecretName, $EntrypointPath

    Invoke-NativeCapture -FilePath "docker" -Arguments @(
        "run", "--rm",
        "--name", $ContainerName,
        "--network", "none",
        "--read-only",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m,mode=0700,uid=1654,gid=1654",
        "--tmpfs", "/run/secrets:rw,noexec,nosuid,size=1m,mode=0700,uid=1654,gid=1654",
        "--mount", "type=volume,source=$SocketVolume,target=/var/run/postgresql",
        "--env", "DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net",
        "--env", "REHEARSAL_CONNECTION=$ConnectionString",
        "--entrypoint", "/bin/sh",
        $RuntimeImage,
        "-ec",
        $migrationCommand) | Out-Null
}

function Wait-RehearsalApiHealth {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$ProbeImage,

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
            $probe = Invoke-NativeCapture -FilePath "docker" -Arguments @(
                "run", "--rm", "--pull", "never",
                "--network", "container:$ContainerName",
                "--read-only",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges",
                "--entrypoint", "/bin/sh",
                $ProbeImage,
                "-ec",
                'wget -q -T 5 -O /dev/null http://127.0.0.1:8080/health/live && wget -q -T 5 -O /dev/null http://127.0.0.1:8080/health/ready') `
                -AllowFailure
            if ($probe.ExitCode -eq 0) {
                return
            }
        }
        elseif ($state.ExitCode -eq 0 -and $state.Output -in @("dead", "exited")) {
            throw "Previous API stopped before its health checks passed."
        }

        Start-Sleep -Seconds 1
    }

    throw "Previous API health checks did not pass within $TimeoutSeconds seconds."
}

$configuration = Get-PostgresBackupConfiguration `
    -EnvironmentFile $EnvironmentFile `
    -LocalRepositoryPath $LocalRepositoryPath `
    -AllowLocalTestResources:$AllowLocalTestResources
$postgresImageEnvironmentName = if ($Workload -eq "AlertReceiver") {
    "GOLDSRCOPS_ALERT_RECEIVER_POSTGRES_IMAGE"
}
else {
    "GOLDSRCOPS_POSTGRES_IMAGE"
}
$runtimeImageEnvironmentName = if ($Workload -eq "AlertReceiver") {
    "GOLDSRCOPS_ALERT_RECEIVER_IMAGE"
}
else {
    "GOLDSRCOPS_IMAGE"
}
$databaseName = if ($Workload -eq "AlertReceiver") { "goldsrcops_receiver" } else { "goldsrcops" }
$databaseUser = if ($Workload -eq "AlertReceiver") { "goldsrcops_receiver" } else { "goldsrcops" }
$migrationHistorySchema = if ($Workload -eq "AlertReceiver") { "receiver" } else { "public" }
$applicationSchema = if ($Workload -eq "AlertReceiver") { "receiver" } else { "goldsrcops" }
$migrationEntrypoint = if ($Workload -eq "AlertReceiver") {
    "/app/receiver-entrypoint.sh"
}
else {
    "/app/api-entrypoint.sh"
}
$databaseSecretName = if ($Workload -eq "AlertReceiver") {
    "receiver-database-connection"
}
else {
    "database-connection"
}

$postgresImage = Get-RequiredDeploymentValue `
    -Values $configuration.Values `
    -Name $postgresImageEnvironmentName
$runtimeImage = Get-RequiredDeploymentValue `
    -Values $configuration.Values `
    -Name $runtimeImageEnvironmentName
Assert-ImmutableBackupImage `
    -Image $postgresImage `
    -Name "PostgreSQL" `
    -AllowLocalTestResources:$AllowLocalTestResources
Assert-ImmutableBackupImage `
    -Image $runtimeImage `
    -Name $Workload `
    -AllowLocalTestResources:$AllowLocalTestResources
if (-not [string]::IsNullOrWhiteSpace($PreviousApiImage)) {
    Assert-BackupCondition `
        -Condition ($Workload -eq "ControlPlane") `
        -Message "PreviousApiImage is valid only for the ControlPlane workload."
    Assert-ImmutableBackupImage `
        -Image $PreviousApiImage `
        -Name "Previous API" `
        -AllowLocalTestResources:$AllowLocalTestResources
    Assert-BackupCondition `
        -Condition ($AllowLocalTestResources -or $PreviousApiImage -cne $runtimeImage) `
        -Message "Previous API image must differ from the candidate API image."
}

$lock = Enter-PostgresBackupLock -Path $LockFile
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$postgresContainer = "goldsrcops-restore-postgres-$runId"
$migrationContainer = "goldsrcops-restore-migration-$runId"
$previousApiContainer = "goldsrcops-restore-previous-api-$runId"
$resticContainer = "goldsrcops-restore-restic-$runId"
$dataVolume = "goldsrcops-restore-data-$runId"
$socketVolume = "goldsrcops-restore-socket-$runId"
$postgresCreated = $false
$dataVolumeCreated = $false
$socketVolumeCreated = $false
$migrationReapplicationVerified = $false
$previousApiStartupVerified = $false
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
        "--env", "POSTGRES_DB=$databaseName",
        "--env", "POSTGRES_USER=$databaseUser",
        "--env", "POSTGRES_HOST_AUTH_METHOD=trust",
        "--env", "POSTGRES_INITDB_ARGS=--auth-local=trust",
        "--mount", "type=volume,source=$dataVolume,target=/var/lib/postgresql/data",
        "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
        $postgresImage) | Out-Null
    $postgresCreated = $true
    Wait-RehearsalPostgres `
        -ContainerName $postgresContainer `
        -DatabaseName $databaseName `
        -DatabaseUser $databaseUser `
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
        "--username=$databaseUser",
        "--dbname=$databaseName",
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

    $rehearsalConnection = "Host=/var/run/postgresql;Port=5432;Database=$databaseName;Username=$databaseUser;SSL Mode=Disable;Timeout=5;Command Timeout=30"
    Invoke-RehearsalMigration `
        -ContainerName $migrationContainer `
        -SocketVolume $socketVolume `
        -RuntimeImage $runtimeImage `
        -EntrypointPath $migrationEntrypoint `
        -DatabaseSecretName $databaseSecretName `
        -ConnectionString $rehearsalConnection

    $migrationCountAfterApply = [int](Invoke-RehearsalSql `
            -ContainerName $postgresContainer `
            -DatabaseName $databaseName `
            -DatabaseUser $databaseUser `
            -Sql "SELECT COUNT(*) FROM $migrationHistorySchema.`"__EFMigrationsHistory`";")
    if ($ReapplyMigration) {
        Invoke-RehearsalMigration `
            -ContainerName $migrationContainer `
            -SocketVolume $socketVolume `
            -RuntimeImage $runtimeImage `
            -EntrypointPath $migrationEntrypoint `
            -DatabaseSecretName $databaseSecretName `
            -ConnectionString $rehearsalConnection
        $migrationCountAfterReapplication = [int](Invoke-RehearsalSql `
                -ContainerName $postgresContainer `
                -DatabaseName $databaseName `
                -DatabaseUser $databaseUser `
                -Sql "SELECT COUNT(*) FROM $migrationHistorySchema.`"__EFMigrationsHistory`";")
        Assert-BackupCondition `
            -Condition ($migrationCountAfterReapplication -eq $migrationCountAfterApply) `
            -Message "Migration reapplication changed the EF Core migration history."
        $migrationReapplicationVerified = $true
    }

    $expectedTables = if ($Workload -eq "AlertReceiver") {
        @("events", "incidents", "provider_outbox_messages")
    }
    else {
        @(
            "availability_incidents",
            "command_executions",
            "game_event_inbox",
            "outbox_messages",
            "outbox_replay_requests",
            "poll_snapshots",
            "server_credentials",
            "server_current_states",
            "servers"
        )
    }
    $tableOutput = Invoke-RehearsalSql `
        -ContainerName $postgresContainer `
        -DatabaseName $databaseName `
        -DatabaseUser $databaseUser `
        -Sql "SELECT table_name FROM information_schema.tables WHERE table_schema = '$applicationSchema' ORDER BY table_name;"
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
            -DatabaseName $databaseName `
            -DatabaseUser $databaseUser `
            -Sql "SELECT COUNT(*) FROM $migrationHistorySchema.`"__EFMigrationsHistory`";")
    $serverCount = if ($Workload -eq "ControlPlane") {
        [int](Invoke-RehearsalSql `
                -ContainerName $postgresContainer `
                -DatabaseName $databaseName `
                -DatabaseUser $databaseUser `
                -Sql 'SELECT COUNT(*) FROM goldsrcops.servers;')
    }
    else {
        0
    }
    $receiverEventCount = if ($Workload -eq "AlertReceiver") {
        [int](Invoke-RehearsalSql `
                -ContainerName $postgresContainer `
                -DatabaseName $databaseName `
                -DatabaseUser $databaseUser `
                -Sql 'SELECT COUNT(*) FROM receiver.events;')
    }
    else {
        0
    }
    $databaseSizeBytes = [long](Invoke-RehearsalSql `
            -ContainerName $postgresContainer `
            -DatabaseName $databaseName `
            -DatabaseUser $databaseUser `
            -Sql "SELECT pg_database_size('$databaseName');")

    Assert-BackupCondition `
        -Condition ($migrationCount -gt 0) `
        -Message "Restored database has no EF Core migration history."
    Assert-BackupCondition `
        -Condition ($serverCount -ge $ExpectedMinimumServerCount) `
        -Message "Restored server count is below the required rehearsal minimum."
    Assert-BackupCondition `
        -Condition ($receiverEventCount -ge $ExpectedMinimumReceiverEventCount) `
        -Message "Restored receiver event count is below the required rehearsal minimum."

    if (-not [string]::IsNullOrWhiteSpace($PreviousApiImage)) {
        Invoke-NativeCapture `
            -FilePath "docker" `
            -Arguments @("image", "inspect", $PreviousApiImage) | Out-Null
        $readOnlyConnection = "$rehearsalConnection;Options=-c default_transaction_read_only=on"
        Invoke-NativeCapture -FilePath "docker" -Arguments @(
            "run", "--detach", "--pull", "never",
            "--name", $previousApiContainer,
            "--network", "none",
            "--read-only",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
            "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
            "--env", "ASPNETCORE_ENVIRONMENT=Production",
            "--env", "ASPNETCORE_HTTP_PORTS=8080",
            "--env", "ConnectionStrings__GoldSrcOps=$readOnlyConnection",
            "--env", "Authentication__Schemes__Bearer__ValidIssuer=https://rehearsal.invalid/",
            "--env", "Authentication__Schemes__Bearer__ValidAudiences__0=goldsrcops-rehearsal",
            "--env", "Polling__Enabled=false",
            "--env", "CommandDispatcher__Enabled=false",
            "--env", "SnapshotRetention__Enabled=false",
            "--env", "AlertDelivery__Enabled=false",
            "--env", "Telemetry__Otlp__Enabled=false",
            "--env", "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false",
            "--entrypoint", "dotnet",
            $PreviousApiImage,
            "GoldSrcOps.Api.dll") | Out-Null
        Wait-RehearsalApiHealth `
            -ContainerName $previousApiContainer `
            -ProbeImage $postgresImage `
            -TimeoutSeconds $StartupTimeoutSeconds
        $previousApiStartupVerified = $true
    }

    $evidence = @{
        Action = "PostgreSQLRestoreRehearsal"
        CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        DatabaseSizeBytes = $databaseSizeBytes
        MigrationCount = $migrationCount
        MigrationReapplicationVerified = $migrationReapplicationVerified
        PostgresImage = $postgresImage
        PreviousApiDatabaseReadOnly = $previousApiStartupVerified
        PreviousApiImage = $PreviousApiImage
        PreviousApiStartupVerified = $previousApiStartupVerified
        ReceiverEventCount = $receiverEventCount
        RequiredTables = $expectedTables
        ResticImage = $configuration.ResticImage
        RuntimeImage = $runtimeImage
        ServerCount = $serverCount
        SnapshotId = [string]$snapshot.id
        SnapshotTime = [string]$snapshot.time
        Workload = $Workload
    }
    if ($Workload -eq "ControlPlane") {
        $evidence.ApiImage = $runtimeImage
    }
    else {
        $evidence.AlertReceiverImage = $runtimeImage
    }
    Write-BackupEvidence -Path $EvidenceFile -Evidence $evidence

    $succeeded = $true
    Write-Host "Encrypted PostgreSQL backup restore rehearsal passed."
    Write-Host "Snapshot: $($snapshot.id)"
    Write-Host "Workload: $Workload; migrations: $migrationCount; servers: $serverCount; receiver events: $receiverEventCount; database bytes: $databaseSizeBytes"
    if ($migrationReapplicationVerified) {
        Write-Host "Migration reapplication: passed"
    }
    if ($previousApiStartupVerified) {
        Write-Host "Previous API read-only startup and health: passed"
    }
}
finally {
    foreach ($container in @($previousApiContainer, $migrationContainer, $resticContainer)) {
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
