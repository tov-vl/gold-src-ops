#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StableZip,
    [Parameter(Mandatory)][string]$Assets,
    [Parameter(Mandatory)][string]$SteamClient,
    [Parameter(Mandatory)][string]$EngineImageId,
    [Parameter(Mandatory)][ValidatePattern('\A[a-zA-Z0-9][a-zA-Z0-9_.-]*\z')][string]$DockerContext,
    [string]$Python = 'python3'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'ops/production/postgres-backup-common.ps1')
$backup = Join-Path $root 'ops/production/player-data-backup.ps1'
$restic = 'restic/restic@sha256:136600b6ff6843d61d355f7f71f460a166429f35de6fd11b568fece3c9a4d510'
$contexts = & docker context inspect $DockerContext | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or @($contexts).Count -ne 1 -or
    $contexts.Endpoints.docker.Host -cnotmatch '\A(?:unix:///(?!/)[^?#]+|npipe:////\./pipe/[\w.-]+)\z') {
    throw 'The rehearsal needs a local Docker context.'
}
& docker --context $DockerContext image inspect $restic | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The pinned restic image must already be present.' }

$tempParent = [IO.Path]::TrimEndingDirectorySeparator(
    (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).ProviderPath)
$temporary = Join-Path $tempParent ('player-recovery-restic-' + [guid]::NewGuid().ToString('N'))
$created = New-Item -ItemType Directory -Path $temporary
$previousContext = $env:DOCKER_CONTEXT
try {
    $env:DOCKER_CONTEXT = $DockerContext
    $repository = Join-Path $temporary 'repository'
    [void](New-Item -ItemType Directory -Path $repository)
    $fixtureCode = 'from pathlib import Path; import sys; sys.path.insert(0, sys.argv[1]); from fixtures import fixture; print(fixture(Path(sys.argv[2]))[1])'
    $hash = & $Python -B -c $fixtureCode $PSScriptRoot $temporary
    if ($LASTEXITCODE -ne 0 -or $hash -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Synthetic vault creation failed.'
    }
    $bundle = Join-Path $temporary 'synthetic.tar'
    $password = Join-Path $temporary 'fixture-password'
    $backend = Join-Path $temporary 'fixture-backend.env'
    $environment = Join-Path $temporary 'fixture.env'
    [IO.File]::WriteAllText($password, 'synthetic-local-repository-only')
    [IO.File]::WriteAllText($backend, 'AWS_DEFAULT_REGION=test')
    if (-not $IsWindows) {
        foreach ($file in @($password, $backend)) { [IO.File]::SetUnixFileMode($file, [IO.UnixFileMode]::UserRead) }
    }
    @(
        "GOLDSRCOPS_RESTIC_IMAGE=$restic"
        'GOLDSRCOPS_BACKUP_HOST=player-recovery-rehearsal'
        'GOLDSRCOPS_BACKUP_REPOSITORY=s3:https://fixture.invalid/bucket'
        "GOLDSRCOPS_RESTIC_PASSWORD_FILE=$password"
        "GOLDSRCOPS_RESTIC_ENVIRONMENT_FILE=$backend"
    ) | Set-Content -LiteralPath $environment -Encoding utf8NoBOM
    $configuration = Get-PostgresBackupConfiguration -EnvironmentFile $environment -LocalRepositoryPath $repository -AllowLocalTestResources
    Invoke-ResticCapture -Configuration $configuration -Arguments @('init') | Out-Null
    $backupOptions = @{
        EnvironmentFile = $environment
        ExpectedSha256 = $hash
        Python = $Python
        LocalRepositoryPath = $repository
        AllowLocalTestResources = $true
        LockFile = Join-Path $temporary 'recovery.lock'
    }
    $archiveDirectory = Join-Path $temporary 'archive'
    $recoverDirectory = Join-Path $temporary 'recover'
    & $backup @backupOptions -Action Archive -BundlePath $bundle -EvidenceDirectory $archiveDirectory | Out-Null
    $archive = Get-Content -Raw -LiteralPath (Join-Path $archiveDirectory 'operation.json') | ConvertFrom-Json
    if ($archive.state -cne 'verified' -or $archive.action -cne 'Archive' -or
        $archive.bundleSha256 -cne $hash -or $archive.snapshotId -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'The Archive receipt does not bind the synthetic snapshot.'
    }
    & $backup @backupOptions -Action Recover -SnapshotId $archive.snapshotId -EvidenceDirectory $recoverDirectory | Out-Null
    $recover = Get-Content -Raw -LiteralPath (Join-Path $recoverDirectory 'operation.json') | ConvertFrom-Json
    $restored = Join-Path $recoverDirectory 'readback/player-data.tar'
    if ($recover.state -cne 'verified' -or $recover.action -cne 'Recover' -or
        $recover.snapshotId -cne $archive.snapshotId -or $recover.bundleSha256 -cne $hash -or
        (Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) {
        throw 'Recover does not match the independently read snapshot and digest.'
    }
    $engineOptions = @(
        '--bundle', $restored, '--expected-sha256', $hash,
        '--stable-zip', $StableZip, '--assets', $Assets, '--steam-client', $SteamClient,
        '--engine-image-id', $EngineImageId, '--docker-context', $DockerContext
    )
    $reportJson = & $Python -B (Join-Path $PSScriptRoot 'check.py') @engineOptions
    $engineExit = $LASTEXITCODE
    $engine = $reportJson | Out-String | ConvertFrom-Json
    if ($engineExit -ne 0 -or $engine.result -cne 'passed' -or -not $engine.containerRemoved -or
        -not $engine.stockPairOpened -or @($engine.epochs).Count -ne 2 -or
        ($engine.epochs | Where-Object { $_.rows -ne 5 -or $_.writes -ne 0 -or $_.failures -ne 0 })) {
        throw "Isolated engine verification failed (exit $engineExit)."
    }
    if ((Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) {
        throw 'The source synthetic TAR changed.'
    }
    $summary = @{
        result = 'passed'
        archive = 'verified'
        recover = 'verified'
        snapshotMatches = $true
        tarSha256Matches = $true
        recoverSeconds = $recover.elapsedSeconds
        engine = $engine
    }
}
finally {
    $env:DOCKER_CONTEXT = $previousContext
    $resolved = Get-Item -LiteralPath $temporary -Force
    if ($resolved.FullName -cne $created.FullName -or $resolved.Parent.FullName -cne $tempParent -or
        ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-ChildItem -LiteralPath $resolved.FullName -Recurse -Force -Attributes ReparsePoint |
            Select-Object -First 1)) {
        throw 'The temporary directory identity changed. Cleanup stopped.'
    }
    Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
}
$summary | ConvertTo-Json -Depth 6
