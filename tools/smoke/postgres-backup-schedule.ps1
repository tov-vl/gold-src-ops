#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runId = [Guid]::NewGuid().ToString("N")
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-backup-status-$runId"
$environmentFile = Join-Path $temporaryDirectory "deployment.env"
$statusFile = Join-Path $temporaryDirectory "status.json"
$statusScript = Join-Path $PSScriptRoot "../../ops/production/postgres-backup-status.ps1"
$snapshotId = "a" * 64

function Write-StatusFile {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Status
    )

    [IO.File]::WriteAllText(
        $statusFile,
        ($Status | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
}

function New-ScheduledStatus {
    param(
        [DateTimeOffset]$CompletedAt = [DateTimeOffset]::UtcNow,
        [string]$BackupHost = "goldsrcops-smoke",
        [bool]$RetentionApplied = $true,
        [int]$KeepDaily = 14
    )

    return @{
        Action = "PostgreSQLBackupCycle"
        BackupHost = $BackupHost
        CompletedAtUtc = $CompletedAt.ToString("O")
        KeepDaily = $KeepDaily
        KeepLast = 3
        KeepMonthly = 12
        KeepWeekly = 8
        ReadDataSubset = "5%"
        RetentionApplied = $RetentionApplied
        RetentionTag = "goldsrcops-postgresql-recoverable"
        SnapshotId = $snapshotId
        SnapshotTime = $CompletedAt.AddMinutes(-1).ToString("O")
    }
}

function Assert-StatusFails {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Check
    )

    $failed = $false
    try {
        & $Check *> $null
    }
    catch {
        $failed = $true
    }

    if (-not $failed) {
        throw "Expected backup status validation to fail."
    }
}

try {
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    [IO.File]::WriteAllText(
        $environmentFile,
        "GOLDSRCOPS_BACKUP_HOST=goldsrcops-smoke`n",
        [Text.UTF8Encoding]::new($false))

    Write-StatusFile -Status (New-ScheduledStatus)
    & $statusScript `
        -EnvironmentFile $environmentFile `
        -StatusFile $statusFile `
        -AllowLocalTestResources

    Write-StatusFile -Status (New-ScheduledStatus -CompletedAt ([DateTimeOffset]::UtcNow.AddHours(-40)))
    Assert-StatusFails {
        & $statusScript `
            -EnvironmentFile $environmentFile `
            -StatusFile $statusFile `
            -MaximumAgeHours 36 `
            -AllowLocalTestResources
    }

    Write-StatusFile -Status (New-ScheduledStatus -CompletedAt ([DateTimeOffset]::UtcNow.AddMinutes(10)))
    Assert-StatusFails {
        & $statusScript `
            -EnvironmentFile $environmentFile `
            -StatusFile $statusFile `
            -AllowLocalTestResources
    }

    Write-StatusFile -Status (New-ScheduledStatus -BackupHost "another-host")
    Assert-StatusFails {
        & $statusScript `
            -EnvironmentFile $environmentFile `
            -StatusFile $statusFile `
            -AllowLocalTestResources
    }

    Write-StatusFile -Status (New-ScheduledStatus -RetentionApplied $false)
    Assert-StatusFails {
        & $statusScript `
            -EnvironmentFile $environmentFile `
            -StatusFile $statusFile `
            -AllowLocalTestResources
    }

    Write-StatusFile -Status (New-ScheduledStatus -KeepDaily 13)
    Assert-StatusFails {
        & $statusScript `
            -EnvironmentFile $environmentFile `
            -StatusFile $statusFile `
            -AllowLocalTestResources
    }

    $oldSnapshotStatus = New-ScheduledStatus
    $oldSnapshotStatus.SnapshotTime = [DateTimeOffset]::UtcNow.AddHours(-5).ToString("O")
    Write-StatusFile -Status $oldSnapshotStatus
    Assert-StatusFails {
        & $statusScript `
            -EnvironmentFile $environmentFile `
            -StatusFile $statusFile `
            -AllowLocalTestResources
    }

    $wrongTagStatus = New-ScheduledStatus
    $wrongTagStatus.RetentionTag = "unrelated"
    Write-StatusFile -Status $wrongTagStatus
    Assert-StatusFails {
        & $statusScript `
            -EnvironmentFile $environmentFile `
            -StatusFile $statusFile `
            -AllowLocalTestResources
    }

    $previewTime = [DateTimeOffset]::UtcNow
    Write-StatusFile -Status @{
        Action = "PostgreSQLBackupRetentionPreview"
        BackupHost = "goldsrcops-smoke"
        CompletedAtUtc = $previewTime.ToString("O")
        KeepDaily = 14
        KeepLast = 3
        KeepMonthly = 12
        KeepWeekly = 8
        LatestSnapshotId = $snapshotId
        LatestSnapshotTime = $previewTime.AddMinutes(-1).ToString("O")
        RetentionApplied = $false
        RetentionTag = "goldsrcops-postgresql-recoverable"
    }
    & $statusScript `
        -EnvironmentFile $environmentFile `
        -StatusFile $statusFile `
        -Kind RetentionPreview `
        -AllowLocalTestResources

    Write-Host "PostgreSQL backup schedule status smoke test passed."
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
        if (-not $resolvedDirectory.StartsWith(
                $resolvedTemporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a schedule smoke directory outside the temporary path."
        }

        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}
