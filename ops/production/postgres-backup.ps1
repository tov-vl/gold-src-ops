#Requires -Version 7.0

<#
.SYNOPSIS
Initializes, creates, or checks encrypted off-host PostgreSQL backups.

.DESCRIPTION
Streams a PostgreSQL custom-format dump directly from the isolated production
container into a client-side encrypted restic repository. No plaintext dump is
written to the host filesystem. A snapshot becomes recoverable only after both
pg_dump and restic exit successfully and the repository structure check passes.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Initialize", "Create", "Check")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$EnvironmentFile,

    [string]$EvidenceFile,

    [string]$LockFile,

    [ValidatePattern('\A(?:100|[1-9]?[0-9])%\z')]
    [string]$ReadDataSubset = "5%",

    [string]$SourceContainer,

    [string]$LocalRepositoryPath,

    [switch]$AllowLocalTestResources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "postgres-backup-common.ps1")

if ([string]::IsNullOrWhiteSpace($LockFile)) {
    $LockFile = Get-DefaultPostgresBackupLockFile
}

Assert-BackupCondition `
    -Condition ($AllowLocalTestResources -or [string]::IsNullOrWhiteSpace($SourceContainer)) `
    -Message "SourceContainer may be overridden only for isolated tests."

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
            Invoke-ResticCapture `
                -Configuration $configuration `
                -Arguments @("check", "--json", "--read-data-subset=$ReadDataSubset") | Out-Null
            Write-Host "PostgreSQL backup repository check passed for data subset $ReadDataSubset."
        }
        "Create" {
            Invoke-ResticCapture -Configuration $configuration -Arguments @("snapshots", "--json") | Out-Null

            if ([string]::IsNullOrWhiteSpace($SourceContainer)) {
                $composeFile = Join-Path $PSScriptRoot "compose.yml"
                $composeResult = Invoke-NativeCapture -FilePath "docker" -Arguments @(
                    "compose",
                    "--env-file", $configuration.EnvironmentFile,
                    "--file", $composeFile,
                    "ps",
                    "--quiet",
                    "postgres")
                $SourceContainer = $composeResult.Output.Trim()
            }

            Assert-BackupCondition `
                -Condition ($SourceContainer -match '\A[0-9a-f]{12,64}\z|\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z') `
                -Message "Could not resolve one valid PostgreSQL source container."

            $runningResult = Invoke-NativeCapture -FilePath "docker" -Arguments @(
                "inspect",
                "--format", "{{.State.Running}}",
                $SourceContainer)
            Assert-BackupCondition `
                -Condition ($runningResult.Output -eq "true") `
                -Message "The PostgreSQL source container is not running."

            $resticContainer = "goldsrcops-restic-backup-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
            $producerArguments = @(
                "exec",
                $SourceContainer,
                "/bin/sh",
                "-ec",
                'export PGPASSWORD="$(cat /run/secrets/postgres-password)"; exec pg_dump --host=/var/run/postgresql --username="$POSTGRES_USER" --dbname="$POSTGRES_DB" --format=custom --no-owner --no-privileges')
            $consumerArguments = New-ResticDockerArguments `
                -Configuration $configuration `
                -ContainerName $resticContainer `
                -Interactive `
                -ResticArguments @(
                    "--retry-lock", "5m",
                    "backup",
                    "--json",
                    "--host", $configuration.BackupHost,
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
                        -Configuration $configuration `
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

            Invoke-ResticCapture -Configuration $configuration -Arguments @("check", "--json") | Out-Null
            $tagResult = Invoke-ResticCapture `
                -Configuration $configuration `
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
                -Configuration $configuration `
                -SnapshotId $recoverableSnapshotId

            $postgresImage = Get-RequiredDeploymentValue `
                -Values $configuration.Values `
                -Name "GOLDSRCOPS_POSTGRES_IMAGE"
            Write-BackupEvidence -Path $EvidenceFile -Evidence @{
                Action = "PostgreSQLBackup"
                ArchiveName = $script:PostgresBackupArchiveName
                BackupHost = $configuration.BackupHost
                CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
                PostgresImage = $postgresImage
                ResticImage = $configuration.ResticImage
                SnapshotId = [string]$snapshot.id
                SnapshotTime = [string]$snapshot.time
            }

            Write-Host "Encrypted off-host PostgreSQL backup created."
            Write-Host "Snapshot: $($snapshot.id)"
        }
    }
}
finally {
    $lock.Dispose()
}
