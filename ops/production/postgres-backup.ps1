#Requires -Version 7.0

<#
.SYNOPSIS
Manages encrypted off-host PostgreSQL backups and their retention schedule.

.DESCRIPTION
Streams a PostgreSQL custom-format dump directly from the isolated production
container into a client-side encrypted restic repository. No plaintext dump is
written to the host filesystem. A snapshot becomes recoverable only after both
pg_dump and restic exit successfully and the repository structure check passes.

Retention is restricted to the configured backup host and recoverable tag. The
Retain action is a dry run unless ApplyRetention is specified. Scheduled always
requires ApplyRetention and publishes its success marker only after backup,
sampled data verification, retention, pruning, and a final repository check.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Initialize", "Create", "Check", "Retain", "Scheduled")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$EnvironmentFile,

    [string]$EvidenceFile,

    [string]$StatusFile,

    [string]$LockFile,

    [ValidatePattern('\A(?:100|[1-9]?[0-9])%\z')]
    [string]$ReadDataSubset = "5%",

    [ValidateRange(3, 365)]
    [int]$KeepLast = 3,

    [ValidateRange(1, 365)]
    [int]$KeepDaily = 14,

    [ValidateRange(1, 104)]
    [int]$KeepWeekly = 8,

    [ValidateRange(1, 120)]
    [int]$KeepMonthly = 12,

    [switch]$ApplyRetention,

    [string]$SourceContainer,

    [string]$LocalRepositoryPath,

    [switch]$AllowLocalTestResources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "postgres-backup-common.ps1")

function Invoke-PostgresRepositoryCheck {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Configuration,

        [Parameter(Mandatory = $true)]
        [string]$DataSubset
    )

    Invoke-ResticCapture `
        -Configuration $Configuration `
        -Arguments @("check", "--json", "--read-data-subset=$DataSubset") | Out-Null
}

function Resolve-PostgresSourceContainer {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Configuration,

        [string]$RequestedContainer
    )

    $resolvedContainer = $RequestedContainer
    if ([string]::IsNullOrWhiteSpace($resolvedContainer)) {
        $composeFile = Join-Path $PSScriptRoot "compose.yml"
        $composeResult = Invoke-NativeCapture -FilePath "docker" -Arguments @(
            "compose",
            "--env-file", $Configuration.EnvironmentFile,
            "--file", $composeFile,
            "ps",
            "--quiet",
            "postgres")
        $resolvedContainer = $composeResult.Output.Trim()
    }

    Assert-BackupCondition `
        -Condition ($resolvedContainer -match '\A[0-9a-f]{12,64}\z|\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z') `
        -Message "Could not resolve one valid PostgreSQL source container."

    $runningResult = Invoke-NativeCapture -FilePath "docker" -Arguments @(
        "inspect",
        "--format", "{{.State.Running}}",
        $resolvedContainer)
    Assert-BackupCondition `
        -Condition ($runningResult.Output -eq "true") `
        -Message "The PostgreSQL source container is not running."

    return $resolvedContainer
}

function New-PostgresBackupSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Configuration,

        [string]$RequestedContainer,

        [string]$BackupEvidenceFile
    )

    Invoke-ResticCapture -Configuration $Configuration -Arguments @("snapshots", "--json") | Out-Null
    $resolvedContainer = Resolve-PostgresSourceContainer `
        -Configuration $Configuration `
        -RequestedContainer $RequestedContainer

    $resticContainer = "goldsrcops-restic-backup-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
    $producerArguments = @(
        "exec",
        $resolvedContainer,
        "/bin/sh",
        "-ec",
        'export PGPASSWORD="$(cat /run/secrets/postgres-password)"; exec pg_dump --host=/var/run/postgresql --username="$POSTGRES_USER" --dbname="$POSTGRES_DB" --format=custom --no-owner --no-privileges')
    $consumerArguments = New-ResticDockerArguments `
        -Configuration $Configuration `
        -ContainerName $resticContainer `
        -Interactive `
        -ResticArguments @(
            "--retry-lock", "5m",
            "backup",
            "--json",
            "--host", $Configuration.BackupHost,
            "--stdin",
            "--stdin-filename", $script:PostgresBackupArchiveName,
            "--tag", $script:PostgresBackupPendingTag)

    $result = Invoke-NativeStreamPipeline `
        -ProducerFilePath "docker" `
        -ProducerArguments $producerArguments `
        -ConsumerFilePath "docker" `
        -ConsumerArguments $consumerArguments
    $snapshotId = if ([string]::IsNullOrWhiteSpace($result.ConsumerOutput)) {
        $null
    }
    else {
        Get-BackupSnapshotId -JsonLines $result.ConsumerOutput
    }

    if ($result.ProducerExitCode -ne 0 -or
        $result.ConsumerExitCode -ne 0 -or
        -not [string]::IsNullOrWhiteSpace($result.StreamError)) {
        if (-not [string]::IsNullOrWhiteSpace($snapshotId)) {
            Invoke-ResticCapture `
                -Configuration $Configuration `
                -Arguments @("forget", $snapshotId) `
                -AllowFailure | Out-Null
        }

        $producerDetail = if ([string]::IsNullOrWhiteSpace($result.ProducerError)) {
            "no pg_dump diagnostic"
        }
        else {
            $result.ProducerError
        }
        $consumerDetail = if ([string]::IsNullOrWhiteSpace($result.ConsumerError)) {
            "no restic diagnostic"
        }
        else {
            $result.ConsumerError
        }
        $streamDetail = if ([string]::IsNullOrWhiteSpace($result.StreamError)) {
            "no stream diagnostic"
        }
        else {
            $result.StreamError
        }

        throw "PostgreSQL backup pipeline failed. pg_dump: $producerDetail; restic: $consumerDetail; stream: $streamDetail"
    }

    Assert-BackupCondition `
        -Condition ($snapshotId -match '\A[0-9a-f]{64}\z') `
        -Message "Restic completed without returning a full snapshot identifier."

    Invoke-ResticCapture -Configuration $Configuration -Arguments @("check", "--json") | Out-Null
    $tagResult = Invoke-ResticCapture `
        -Configuration $Configuration `
        -Arguments @(
            "--json",
            "tag",
            "--remove", $script:PostgresBackupPendingTag,
            "--add", $script:PostgresBackupRecoverableTag,
            $snapshotId)
    $recoverableSnapshotId = Get-RetaggedBackupSnapshotId `
        -JsonLines $tagResult.Output `
        -OriginalSnapshotId $snapshotId
    Assert-BackupCondition `
        -Condition ($recoverableSnapshotId -match '\A[0-9a-f]{64}\z') `
        -Message "Restic did not return the recoverable snapshot identifier after retagging."
    $snapshot = Get-RecoverableBackupSnapshot `
        -Configuration $Configuration `
        -SnapshotId $recoverableSnapshotId

    $postgresImage = Get-RequiredDeploymentValue `
        -Values $Configuration.Values `
        -Name "GOLDSRCOPS_POSTGRES_IMAGE"
    Write-BackupEvidence -Path $BackupEvidenceFile -Evidence @{
        Action = "PostgreSQLBackup"
        ArchiveName = $script:PostgresBackupArchiveName
        BackupHost = $Configuration.BackupHost
        CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        PostgresImage = $postgresImage
        ResticImage = $Configuration.ResticImage
        SnapshotId = [string]$snapshot.id
        SnapshotTime = [string]$snapshot.time
    }

    Write-Host "Encrypted off-host PostgreSQL backup created."
    Write-Host "Snapshot: $($snapshot.id)"
    return $snapshot
}

function Invoke-PostgresBackupRetention {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Configuration,

        [Parameter(Mandatory = $true)]
        [int]$RetainLast,

        [Parameter(Mandatory = $true)]
        [int]$RetainDaily,

        [Parameter(Mandatory = $true)]
        [int]$RetainWeekly,

        [Parameter(Mandatory = $true)]
        [int]$RetainMonthly,

        [switch]$Apply
    )

    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add("--retry-lock")
    $arguments.Add("5m")
    $arguments.Add("forget")
    $arguments.Add("--host")
    $arguments.Add($Configuration.BackupHost)
    $arguments.Add("--tag")
    $arguments.Add($script:PostgresBackupRecoverableTag)
    $arguments.Add("--group-by")
    $arguments.Add("host,paths,tags")
    $arguments.Add("--keep-last")
    $arguments.Add([string]$RetainLast)
    $arguments.Add("--keep-daily")
    $arguments.Add([string]$RetainDaily)
    $arguments.Add("--keep-weekly")
    $arguments.Add([string]$RetainWeekly)
    $arguments.Add("--keep-monthly")
    $arguments.Add([string]$RetainMonthly)
    if ($Apply) {
        $arguments.Add("--prune")
    }
    else {
        $arguments.Add("--dry-run")
    }

    $result = Invoke-ResticCapture `
        -Configuration $Configuration `
        -Arguments $arguments.ToArray()
    if (-not $Apply -and -not [string]::IsNullOrWhiteSpace($result.Output)) {
        Write-Host $result.Output
    }

    if ($Apply) {
        Invoke-ResticCapture -Configuration $Configuration -Arguments @("check", "--json") | Out-Null
        Write-Host "PostgreSQL backup retention and prune completed."
    }
    else {
        Write-Host "PostgreSQL backup retention preview completed without changes."
    }

    return [pscustomobject]@{
        Applied = [bool]$Apply
        KeepDaily = $RetainDaily
        KeepLast = $RetainLast
        KeepMonthly = $RetainMonthly
        KeepWeekly = $RetainWeekly
    }
}

function Write-PostgresBackupRetentionEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Configuration,

        [Parameter(Mandatory = $true)]
        [object]$Retention,

        [string]$Path
    )

    $latestSnapshot = Get-RecoverableBackupSnapshot -Configuration $Configuration
    $actionName = if ($Retention.Applied) {
        "PostgreSQLBackupRetentionApply"
    }
    else {
        "PostgreSQLBackupRetentionPreview"
    }

    Write-BackupEvidence -Path $Path -Evidence @{
        Action = $actionName
        BackupHost = $Configuration.BackupHost
        CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        KeepDaily = $Retention.KeepDaily
        KeepLast = $Retention.KeepLast
        KeepMonthly = $Retention.KeepMonthly
        KeepWeekly = $Retention.KeepWeekly
        LatestSnapshotId = [string]$latestSnapshot.id
        LatestSnapshotTime = [string]$latestSnapshot.time
        RetentionApplied = [bool]$Retention.Applied
        RetentionTag = $script:PostgresBackupRecoverableTag
    }
}

if ([string]::IsNullOrWhiteSpace($LockFile)) {
    $LockFile = Get-DefaultPostgresBackupLockFile
}

Assert-BackupCondition `
    -Condition ($AllowLocalTestResources -or [string]::IsNullOrWhiteSpace($SourceContainer)) `
    -Message "SourceContainer may be overridden only for isolated tests."
Assert-BackupCondition `
    -Condition (-not $ApplyRetention -or $Action -in @("Retain", "Scheduled")) `
    -Message "ApplyRetention is valid only for Retain or Scheduled."
Assert-BackupCondition `
    -Condition ($Action -ne "Scheduled" -or $ApplyRetention) `
    -Message "Scheduled requires explicit ApplyRetention."
Assert-BackupCondition `
    -Condition ($Action -ne "Scheduled" -or -not [string]::IsNullOrWhiteSpace($StatusFile)) `
    -Message "Scheduled requires StatusFile for its atomic success marker."

$configuration = Get-PostgresBackupConfiguration `
    -EnvironmentFile $EnvironmentFile `
    -LocalRepositoryPath $LocalRepositoryPath `
    -AllowLocalTestResources:$AllowLocalTestResources
$lock = Enter-PostgresBackupLock -Path $LockFile

try {
    switch ($Action) {
        "Initialize" {
            Invoke-ResticCapture -Configuration $configuration -Arguments @("init") | Out-Null
            Invoke-ResticCapture -Configuration $configuration -Arguments @("check", "--json") | Out-Null
            Write-Host "Encrypted PostgreSQL backup repository initialized and checked."
        }
        "Check" {
            Invoke-PostgresRepositoryCheck `
                -Configuration $configuration `
                -DataSubset $ReadDataSubset
            Write-Host "PostgreSQL backup repository check passed for data subset $ReadDataSubset."
        }
        "Create" {
            New-PostgresBackupSnapshot `
                -Configuration $configuration `
                -RequestedContainer $SourceContainer `
                -BackupEvidenceFile $EvidenceFile | Out-Null
        }
        "Retain" {
            $retention = Invoke-PostgresBackupRetention `
                -Configuration $configuration `
                -RetainLast $KeepLast `
                -RetainDaily $KeepDaily `
                -RetainWeekly $KeepWeekly `
                -RetainMonthly $KeepMonthly `
                -Apply:$ApplyRetention
            Write-PostgresBackupRetentionEvidence `
                -Configuration $configuration `
                -Retention $retention `
                -Path $StatusFile
        }
        "Scheduled" {
            $snapshot = New-PostgresBackupSnapshot `
                -Configuration $configuration `
                -RequestedContainer $SourceContainer `
                -BackupEvidenceFile $EvidenceFile
            Invoke-PostgresRepositoryCheck `
                -Configuration $configuration `
                -DataSubset $ReadDataSubset
            $retention = Invoke-PostgresBackupRetention `
                -Configuration $configuration `
                -RetainLast $KeepLast `
                -RetainDaily $KeepDaily `
                -RetainWeekly $KeepWeekly `
                -RetainMonthly $KeepMonthly `
                -Apply

            Write-BackupEvidence -Path $StatusFile -Evidence @{
                Action = "PostgreSQLBackupCycle"
                BackupHost = $configuration.BackupHost
                CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
                KeepDaily = $retention.KeepDaily
                KeepLast = $retention.KeepLast
                KeepMonthly = $retention.KeepMonthly
                KeepWeekly = $retention.KeepWeekly
                ReadDataSubset = $ReadDataSubset
                ResticImage = $configuration.ResticImage
                RetentionApplied = $true
                RetentionTag = $script:PostgresBackupRecoverableTag
                SnapshotId = [string]$snapshot.id
                SnapshotTime = [string]$snapshot.time
            }
            Write-Host "Scheduled PostgreSQL backup cycle completed."
        }
    }
}
finally {
    $lock.Dispose()
}
