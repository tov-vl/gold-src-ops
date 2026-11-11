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
$archive = Join-Path $output "availability-diagnostics-$Version.zip"
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath "$archive.sha256")) { throw 'Immutable output already exists.' }
$temporary = Join-Path $output ('.diagnostics-package-' + [guid]::NewGuid().ToString('N'))
$files = @(
    '.github/workflows/availability-shadow-audit.yml',
    'ops/availability/run-shadow-audit.ps1',
    'ops/availability/completeness-report.py',
    'tools/smoke/availability-shadow-audit.ps1',
    'tools/smoke/availability-completeness.py',
    'docs/v2.119-archive-completeness.md',
    'docs/release-notes-v2.119.md')
try {
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    $payload = foreach ($name in $files) {
        $path = Join-Path $temporary $name
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        # Package canonical Git text, independently of Windows checkout newlines.
        $content = [IO.File]::ReadAllText((Join-Path $root $name)).Replace("`r`n", "`n")
        [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
        [ordered]@{ path=$name; length=(Get-Item -LiteralPath $path).Length
            sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $manifest = [ordered]@{ schemaVersion=1; purpose='availability-diagnostics'; version=$Version
        sourceRevision=$revision; payload=@($payload); diagnosticsRevision='archive-completeness-v1'
        exporterChanged=$false; sliPolicyChanged=$false; archiveWrites=$false }
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
