#Requires -Version 7.0

<#
.SYNOPSIS
Archives a closed player-data bundle using the existing encrypted restic backend.
.DESCRIPTION
Archive uploads once and reads back the exact snapshot. Recover reads only an
explicit snapshot. Both validate every byte and extract into a new private
directory. No game service, live vault, repository initialization or retention
is changed. An incomplete evidence directory is an uncertain operation, never
permission to retry Archive automatically.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Archive', 'Recover')][string]$Action,
    [Parameter(Mandatory)][string]$EnvironmentFile,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{64}\z')][string]$ExpectedSha256,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$BundlePath,
    [ValidatePattern('\A[0-9a-f]{64}\z')][string]$SnapshotId,
    [string]$Python = 'python3',
    [string]$LocalRepositoryPath,
    [string]$LockFile,
    [switch]$AllowLocalTestResources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'postgres-backup-common.ps1')
$bundleTool = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../gameserver/player-data-bundle.py'))
$archiveName = 'player-data.tar'
$archiveTag = 'goldsrcops-player-data-v1'

function Invoke-PlayerProcess {
    param([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds = 600, [switch]$AllowFailure)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-NativeProcessStartInfo -FilePath $Executable -Arguments $Arguments -RedirectOutput
    try {
        if (-not $process.Start()) { throw 'Player backup process could not start.' }
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw 'Player backup process timed out; reconcile before retry.'
        }
        $output = $outTask.GetAwaiter().GetResult()
        [void]$errTask.GetAwaiter().GetResult()
        if (-not $AllowFailure -and $process.ExitCode -ne 0) { throw 'Player backup process failed; retain the private operation record.' }
        return $output
    }
    finally { $process.Dispose() }
}

function Invoke-PlayerRestic {
    param([string[]]$Arguments, [string[]]$Mounts = @())
    $name = 'goldsrcops-player-backup-' + [Guid]::NewGuid().ToString('N')
    $dockerArguments = [Collections.Generic.List[string]]::new()
    $dockerArguments.AddRange([string[]](New-ResticDockerArguments -Configuration $configuration `
        -ContainerName $name -ResticArguments $Arguments))
    $index = $dockerArguments.IndexOf($configuration.ResticImage)
    foreach ($mount in $Mounts) {
        $dockerArguments.Insert($index++, '--mount')
        $dockerArguments.Insert($index++, $mount)
    }
    try { return Invoke-PlayerProcess -Executable 'docker' -Arguments $dockerArguments.ToArray() }
    finally {
        # This unique container belongs to this operation, including after timeout.
        [void](Invoke-PlayerProcess -Executable 'docker' -Arguments @('rm', '--force', $name) -TimeoutSeconds 30 -AllowFailure)
    }
}

function Test-PlayerBundle {
    param([string]$Path)
    [void](Invoke-PlayerProcess -Executable $Python -Arguments @($bundleTool, 'inspect',
        '--bundle', $Path, '--expected-sha256', $ExpectedSha256) -TimeoutSeconds 30)
}

if (($Action -eq 'Archive' -and ([string]::IsNullOrWhiteSpace($BundlePath) -or $SnapshotId)) -or
    ($Action -eq 'Recover' -and ([string]::IsNullOrWhiteSpace($SnapshotId) -or $BundlePath))) {
    throw 'Archive requires only BundlePath; Recover requires only an exact SnapshotId.'
}
$configuration = Get-PostgresBackupConfiguration -EnvironmentFile $EnvironmentFile `
    -LocalRepositoryPath $LocalRepositoryPath -AllowLocalTestResources:$AllowLocalTestResources
$backupHost = $configuration.BackupHost + '.players'
if ($backupHost.Length -gt 253) { throw 'Player backup host identifier is too long.' }
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (-not $AllowLocalTestResources -and (Test-PathInsideRepository $EvidenceDirectory)) {
    throw 'Private player-data evidence must be outside the repository.'
}
if ($EvidenceDirectory.Contains(',')) { throw 'Evidence directory is not a safe Docker mount.' }
if ($Action -eq 'Archive') {
    $BundlePath = (Resolve-Path -LiteralPath $BundlePath).Path
    if ($BundlePath.Contains(',')) { throw 'Bundle path is not a safe Docker mount.' }
    Test-PlayerBundle $BundlePath
    if (-not $AllowLocalTestResources) {
        [void](Assert-BackupSecretFile -Path $BundlePath -Name 'Private player-data bundle')
    }
}
if (-not $LockFile) { $LockFile = Get-DefaultPostgresBackupLockFile }
$operationLock = Enter-PostgresBackupLock -Path $LockFile
try {
    # mkdir is atomic and rejects existing destinations, including symlinks.
    [void](Invoke-PlayerProcess -Executable $Python -Arguments @('-c',
        'import pathlib,sys; p=pathlib.Path(sys.argv[1]); assert not any(x.is_symlink() for x in p.parents); p.mkdir(mode=0o700)',
        $EvidenceDirectory) -TimeoutSeconds 30)
    $started = [DateTimeOffset]::UtcNow
    $operation = @{ schemaVersion = 1; action = $Action; state = 'started';
        startedUtc = $started.ToString('O'); bundleSha256 = $ExpectedSha256 }
    $recordPath = Join-Path $EvidenceDirectory 'operation.json'
    $operation | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding utf8NoBOM
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($recordPath, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
    if ($Action -eq 'Archive') {
        $output = Invoke-PlayerRestic -Arguments @('backup', '--json', '--host', $backupHost,
            '--tag', $archiveTag, '--tag', "bundle-sha256-$ExpectedSha256", "/$archiveName") `
            -Mounts @("type=bind,source=$BundlePath,target=/$archiveName,readonly")
        $SnapshotId = Get-BackupSnapshotId -JsonLines $output
        if ($SnapshotId -notmatch '\A[0-9a-f]{64}\z') { throw 'Exact archived snapshot identity is missing; reconcile before retry.' }
        $operation.snapshotId = $SnapshotId
        $operation.state = 'uploaded'
        $operation | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding utf8NoBOM
        Test-PlayerBundle $BundlePath
    }
    $snapshots = @(Invoke-PlayerRestic -Arguments @('snapshots', '--json', $SnapshotId) | ConvertFrom-Json)
    if ($snapshots.Count -ne 1 -or $snapshots[0].id -cne $SnapshotId -or
        $snapshots[0].hostname -cne $backupHost -or
        @($snapshots[0].paths).Count -ne 1 -or $snapshots[0].paths[0] -cne "/$archiveName" -or
        $snapshots[0].tags -cnotcontains $archiveTag -or
        $snapshots[0].tags -cnotcontains "bundle-sha256-$ExpectedSha256") {
        throw 'Snapshot identity, workload, path or bundle binding does not match.'
    }
    [void](Invoke-PlayerRestic -Arguments @('check'))
    # Container sees only this new empty target. The existing archive is never overwritten.
    $readbackDirectory = Join-Path $EvidenceDirectory 'readback'
    [void](Invoke-PlayerProcess -Executable $Python -Arguments @('-c',
        'import pathlib,sys; pathlib.Path(sys.argv[1]).mkdir(mode=0o700)', $readbackDirectory))
    [void](Invoke-PlayerRestic -Arguments @('restore', $SnapshotId, '--target', '/restore',
        '--include', "/$archiveName", '--verify') `
        -Mounts @("type=bind,source=$readbackDirectory,target=/restore"))
    $readback = Join-Path $readbackDirectory $archiveName
    Test-PlayerBundle $readback
    [void](Invoke-PlayerProcess -Executable $Python -Arguments @($bundleTool, 'restore',
        '--bundle', $readback, '--expected-sha256', $ExpectedSha256,
        '--target', (Join-Path $EvidenceDirectory 'vault')))
    $operation.state = 'verified'
    $operation.snapshotId = $SnapshotId
    $operation.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $operation.elapsedSeconds = [math]::Round(([DateTimeOffset]::UtcNow - $started).TotalSeconds, 2)
    $operation.engineAcceptance = 'pending'
    $operation | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding utf8NoBOM
    Write-Output 'PLAYER_DATA_OFFHOST_BYTES=passed; engine acceptance remains separate'
}
finally { $operationLock.Dispose() }
