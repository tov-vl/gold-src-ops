#Requires -Version 7.0

<#
.SYNOPSIS
Validates PostgreSQL backup schedule evidence and freshness.

.DESCRIPTION
Checks a sanitized owner-only evidence file without opening the backup
repository. ScheduledCycle proves the last complete backup, data check,
retention, prune, and final repository check. RetentionPreview is the mandatory
non-destructive gate before installing the production timer.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$EnvironmentFile,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$StatusFile,

    [ValidateSet("ScheduledCycle", "RetentionPreview")]
    [string]$Kind = "ScheduledCycle",

    [ValidateRange(1, 168)]
    [int]$MaximumAgeHours = 36,

    [ValidateRange(0, 60)]
    [int]$FutureToleranceMinutes = 5,

    [ValidateRange(3, 365)]
    [int]$ExpectedKeepLast = 3,

    [ValidateRange(1, 365)]
    [int]$ExpectedKeepDaily = 14,

    [ValidateRange(1, 104)]
    [int]$ExpectedKeepWeekly = 8,

    [ValidateRange(1, 120)]
    [int]$ExpectedKeepMonthly = 12,

    [ValidatePattern('\A(?:100|[1-9]?[0-9])%\z')]
    [string]$ExpectedReadDataSubset = "5%",

    [switch]$AllowLocalTestResources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "postgres-backup-common.ps1")

function Get-RequiredStatusValue {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Status,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $property = $Status.PSObject.Properties[$Name]
    Assert-BackupCondition `
        -Condition ($null -ne $property -and $null -ne $property.Value) `
        -Message "Backup status is missing '$Name'."
    return $property.Value
}

function ConvertTo-StatusTimestamp {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $timestamp = [DateTimeOffset]::MinValue
    $parsed = [DateTimeOffset]::TryParse(
        [string]$Value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$timestamp)
    Assert-BackupCondition `
        -Condition $parsed `
        -Message "Backup status '$Name' is not a valid round-trip timestamp."
    return $timestamp
}

$resolvedStatusFile = (Resolve-Path -LiteralPath $StatusFile).Path
Assert-BackupCondition `
    -Condition (Test-Path -LiteralPath $resolvedStatusFile -PathType Leaf) `
    -Message "Backup status file does not exist."
if (-not $AllowLocalTestResources) {
    Assert-BackupCondition `
        -Condition (-not (Test-PathInsideRepository -Path $resolvedStatusFile)) `
        -Message "Backup status must live outside the repository."
}

if (-not $IsWindows -and -not $AllowLocalTestResources) {
    $mode = [IO.File]::GetUnixFileMode($resolvedStatusFile)
    $forbiddenMode =
        [IO.UnixFileMode]::GroupRead -bor
        [IO.UnixFileMode]::GroupWrite -bor
        [IO.UnixFileMode]::GroupExecute -bor
        [IO.UnixFileMode]::OtherRead -bor
        [IO.UnixFileMode]::OtherWrite -bor
        [IO.UnixFileMode]::OtherExecute
    Assert-BackupCondition `
        -Condition (($mode -band $forbiddenMode) -eq 0) `
        -Message "Backup status must not be accessible by group or other users."
}

try {
    $status = Get-Content -LiteralPath $resolvedStatusFile -Raw | ConvertFrom-Json -Depth 20
}
catch {
    throw "Backup status is not valid JSON. $($_.Exception.Message)"
}

$environment = Read-DeploymentEnvironment -Path $EnvironmentFile
$expectedBackupHost = Get-RequiredDeploymentValue `
    -Values $environment.Values `
    -Name "GOLDSRCOPS_BACKUP_HOST"
$backupHost = [string](Get-RequiredStatusValue -Status $status -Name "BackupHost")
Assert-BackupCondition `
    -Condition ($backupHost.Equals($expectedBackupHost, [StringComparison]::Ordinal)) `
    -Message "Backup status belongs to a different backup host."
$retentionTag = [string](Get-RequiredStatusValue -Status $status -Name "RetentionTag")
Assert-BackupCondition `
    -Condition ($retentionTag -eq $script:PostgresBackupRecoverableTag) `
    -Message "Backup status retention tag does not match the recoverable backup scope."

$expectedAction = if ($Kind -eq "ScheduledCycle") {
    "PostgreSQLBackupCycle"
}
else {
    "PostgreSQLBackupRetentionPreview"
}
$action = [string](Get-RequiredStatusValue -Status $status -Name "Action")
Assert-BackupCondition `
    -Condition ($action -eq $expectedAction) `
    -Message "Backup status action does not match '$Kind'."

$completedAt = ConvertTo-StatusTimestamp `
    -Value (Get-RequiredStatusValue -Status $status -Name "CompletedAtUtc") `
    -Name "CompletedAtUtc"
$now = [DateTimeOffset]::UtcNow
Assert-BackupCondition `
    -Condition ($completedAt -le $now.AddMinutes($FutureToleranceMinutes)) `
    -Message "Backup status completion time is unexpectedly in the future."
$age = $now - $completedAt
Assert-BackupCondition `
    -Condition ($age -le [TimeSpan]::FromHours($MaximumAgeHours)) `
    -Message "Backup status is stale."

$policy = @{
    KeepDaily = $ExpectedKeepDaily
    KeepLast = $ExpectedKeepLast
    KeepMonthly = $ExpectedKeepMonthly
    KeepWeekly = $ExpectedKeepWeekly
}
foreach ($entry in $policy.GetEnumerator()) {
    $actualValue = Get-RequiredStatusValue -Status $status -Name $entry.Key
    Assert-BackupCondition `
        -Condition ([int]$actualValue -eq $entry.Value) `
        -Message "Backup status retention policy does not match '$($entry.Key)'."
}

$retentionApplied = Get-RequiredStatusValue -Status $status -Name "RetentionApplied"
Assert-BackupCondition `
    -Condition ($retentionApplied -is [bool]) `
    -Message "Backup status RetentionApplied must be a boolean."

if ($Kind -eq "ScheduledCycle") {
    Assert-BackupCondition `
        -Condition ([bool]$retentionApplied) `
        -Message "Scheduled backup status does not prove applied retention."
    $snapshotId = [string](Get-RequiredStatusValue -Status $status -Name "SnapshotId")
    Assert-BackupCondition `
        -Condition ($snapshotId -match '\A[0-9a-f]{64}\z') `
        -Message "Scheduled backup status contains an invalid snapshot identifier."
    $snapshotTime = ConvertTo-StatusTimestamp `
        -Value (Get-RequiredStatusValue -Status $status -Name "SnapshotTime") `
        -Name "SnapshotTime"
    Assert-BackupCondition `
        -Condition ($snapshotTime -le $completedAt.AddMinutes($FutureToleranceMinutes)) `
        -Message "Scheduled backup snapshot time is later than cycle completion."
    Assert-BackupCondition `
        -Condition ($snapshotTime -ge $completedAt.AddHours(-4)) `
        -Message "Scheduled backup snapshot is older than the bounded cycle duration."
    $readDataSubset = [string](Get-RequiredStatusValue -Status $status -Name "ReadDataSubset")
    Assert-BackupCondition `
        -Condition ($readDataSubset -eq $ExpectedReadDataSubset) `
        -Message "Scheduled backup status data-check policy does not match."
}
else {
    Assert-BackupCondition `
        -Condition (-not [bool]$retentionApplied) `
        -Message "Retention preview status unexpectedly reports applied changes."
    $snapshotId = [string](Get-RequiredStatusValue -Status $status -Name "LatestSnapshotId")
    Assert-BackupCondition `
        -Condition ($snapshotId -match '\A[0-9a-f]{64}\z') `
        -Message "Retention preview status contains an invalid snapshot identifier."
    [void](ConvertTo-StatusTimestamp `
            -Value (Get-RequiredStatusValue -Status $status -Name "LatestSnapshotTime") `
            -Name "LatestSnapshotTime")
}

$ageHours = [Math]::Max(0, $age.TotalHours).ToString(
    "0.00",
    [Globalization.CultureInfo]::InvariantCulture)
$shortSnapshotId = $snapshotId.Substring(0, 12)
Write-Host "PostgreSQL backup status passed: kind=$Kind ageHours=$ageHours snapshot=$shortSnapshotId."
