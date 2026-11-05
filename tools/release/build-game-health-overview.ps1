#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40}$') { throw 'Source revision unavailable.' }
if (& git -C $root status --porcelain) { throw 'Source must be clean, including untracked inputs.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$archive = Join-Path $output "game-health-overview-$Version.zip"
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath "$archive.sha256")) { throw 'Immutable output already exists.' }
$temporary = Join-Path $output ('.monitor-package-' + [guid]::NewGuid().ToString('N'))
$files = @(
    'ops/production/game-health-dashboard.json',
    'ops/production/game-health-dashboard.py',
    'ops/production/agent-monitor-dashboard.py')
try {
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    $payload = foreach ($name in $files) {
        $path = Join-Path $temporary $name
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        # Package canonical Git bytes, independent of Windows checkout EOLs.
        $start = [Diagnostics.ProcessStartInfo]::new('git')
        $start.WorkingDirectory = $root
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('cat-file', 'blob', "${revision}:$name")) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try { $process.StandardOutput.BaseStream.CopyTo($stream) }
        finally { $stream.Dispose() }
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $exitCode = $process.ExitCode
        $process.Dispose()
        if ($exitCode -ne 0) { throw "Canonical source unavailable: $name. $errorText" }
        [ordered]@{ path=$name; length=(Get-Item -LiteralPath $path).Length
            sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            mode=$(if ($name.EndsWith('.sh',[StringComparison]::Ordinal)) { '0755' } else { '0644' }) }
    }
    $manifest = [ordered]@{ schemaVersion=1; purpose='game-health-overview'; version=$Version
        sourceRevision=$revision; payload=@($payload)
        dashboardUid='goldsrcops-game-host-overview' }
    [IO.File]::WriteAllText((Join-Path $temporary 'manifest.json'), (($manifest | ConvertTo-Json -Depth 8) + "`n").Replace("`r`n","`n"), [Text.UTF8Encoding]::new($false))
    [IO.Compression.ZipFile]::CreateFromDirectory($temporary,$archive)
    $hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$archive.sha256","$hash  $([IO.Path]::GetFileName($archive))`n",[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{Path=$archive;Sha256=$hash;SourceRevision=$revision;Version=$Version}
}
finally {
    $resolved=[IO.Path]::GetFullPath($temporary)
    if ($resolved.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
