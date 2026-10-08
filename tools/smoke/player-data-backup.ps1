#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Python = 'python3',
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('player-backup-test-' + [Guid]::NewGuid().ToString('N')))
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'ops/production/postgres-backup-common.ps1')
$scriptPath = Join-Path $root 'ops/production/player-data-backup.ps1'
$tool = Join-Path $root 'ops/gameserver/player-data-bundle.py'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Smoke output already exists.' }
[void](New-Item -ItemType Directory -Path $OutputDirectory)
$repository = Join-Path $OutputDirectory 'repository'
$source = Join-Path $OutputDirectory 'source'
[void](New-Item -ItemType Directory -Path $repository, $source)
foreach ($stem in @('goldsrcops-player-stats-v1', 'goldsrcops-player-preferences-v1')) {
    [IO.File]::WriteAllBytes((Join-Path $source "$stem.vault"), [byte[]]@(0, 255, 13, 10, 80, 77, 73, 2))
    [IO.File]::WriteAllBytes((Join-Path $source "$stem.journal"), [byte[]]@(0, 1, 2, 13, 10, 255))
}
$bundle = Join-Path $OutputDirectory 'source.tar'
& $Python $tool create --vault-directory $source --output $bundle
if ($LASTEXITCODE -ne 0) { throw 'Synthetic bundle creation failed.' }
$hash = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
$password = Join-Path $OutputDirectory 'test-password'
$backend = Join-Path $OutputDirectory 'test-backend.env'
$environment = Join-Path $OutputDirectory 'test.env'
[IO.File]::WriteAllText($password, 'synthetic-local-repository-only')
[IO.File]::WriteAllText($backend, 'AWS_DEFAULT_REGION=test')
if (-not $IsWindows) {
    foreach ($file in @($password, $backend)) { [IO.File]::SetUnixFileMode($file, [IO.UnixFileMode]::UserRead) }
}
@(
    'GOLDSRCOPS_RESTIC_IMAGE=restic/restic@sha256:136600b6ff6843d61d355f7f71f460a166429f35de6fd11b568fece3c9a4d510'
    'GOLDSRCOPS_BACKUP_HOST=player-data-fixture'
    'GOLDSRCOPS_BACKUP_REPOSITORY=s3:https://fixture.invalid/bucket'
    "GOLDSRCOPS_RESTIC_PASSWORD_FILE=$password"
    "GOLDSRCOPS_RESTIC_ENVIRONMENT_FILE=$backend"
) | Set-Content -LiteralPath $environment -Encoding utf8NoBOM
$configuration = Get-PostgresBackupConfiguration -EnvironmentFile $environment `
    -LocalRepositoryPath $repository -AllowLocalTestResources
Invoke-ResticCapture -Configuration $configuration -Arguments @('init') | Out-Null
$arguments = @{ EnvironmentFile = $environment; ExpectedSha256 = $hash; Python = $Python;
    LocalRepositoryPath = $repository; AllowLocalTestResources = $true;
    LockFile = (Join-Path $OutputDirectory 'recovery.lock') }
$archiveEvidence = Join-Path $OutputDirectory 'archive'
& $scriptPath @arguments -Action Archive -BundlePath $bundle -EvidenceDirectory $archiveEvidence
$record = Get-Content -Raw -LiteralPath (Join-Path $archiveEvidence 'operation.json') | ConvertFrom-Json
if ($record.state -ne 'verified' -or $record.engineAcceptance -ne 'pending') { throw 'Archive evidence mismatch.' }
$restoreEvidence = Join-Path $OutputDirectory 'recover'
& $scriptPath @arguments -Action Recover -SnapshotId $record.snapshotId -EvidenceDirectory $restoreEvidence
foreach ($file in Get-ChildItem -LiteralPath $source -File) {
    $restored = Join-Path $restoreEvidence "vault/$($file.Name)"
    if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash $restored).Hash) { throw 'Roundtrip changed synthetic bytes.' }
}
try {
    & $scriptPath @arguments -Action Recover -SnapshotId $record.snapshotId -EvidenceDirectory $restoreEvidence
    throw 'Existing recovery directory was accepted.'
}
catch {
    if ($_.Exception.Message -ne 'Player backup process failed; retain the private operation record.') { throw }
}
$wrong = $arguments.Clone()
$wrong.ExpectedSha256 = '0' * 64
try {
    & $scriptPath @wrong -Action Recover -SnapshotId $record.snapshotId -EvidenceDirectory (Join-Path $OutputDirectory 'wrong-binding')
    throw 'Incorrect snapshot binding was accepted.'
}
catch {
    if ($_.Exception.Message -ne 'Snapshot identity, workload, path or bundle binding does not match.') { throw }
}
$held = Enter-PostgresBackupLock -Path $arguments.LockFile
try {
    try {
        & $scriptPath @arguments -Action Recover -SnapshotId $record.snapshotId -EvidenceDirectory (Join-Path $OutputDirectory 'locked')
        throw 'Concurrent recovery was accepted.'
    }
    catch {
        if ($_.Exception.Message -notlike 'Another PostgreSQL backup or restore action already holds*') { throw }
    }
}
finally { $held.Dispose() }
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'locked')) { throw 'Lock failure created output.' }
if ((Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'Source bundle changed.' }
Write-Output 'PLAYER_DATA_RESTIC_SMOKE=passed encrypted_roundtrip=1 source_unchanged=1 overwrite_refused=1 binding_refused=1 concurrent_refused=1 network=none'
