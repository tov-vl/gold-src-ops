#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.]+)?$')][string]$Version,
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
$archive = Join-Path $output "game-event-agent-$Version-linux-x64.zip"
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath "$archive.sha256")) { throw 'Immutable output already exists.' }
$temporary = Join-Path $output ('.agent-package-' + [guid]::NewGuid().ToString('N'))
try {
    $publish = Join-Path $temporary 'publish'
    & dotnet publish (Join-Path $root 'src/GoldSrcOps.GameEventAgent/GoldSrcOps.GameEventAgent.csproj') -c Release -p:GameEventPilotBundle=true -p:SourceRevisionId=$revision -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
    $binary = Join-Path $publish 'GoldSrcOps.GameEventAgent'
    $content = Join-Path $temporary 'content'
    [IO.Directory]::CreateDirectory((Join-Path $content 'agent')) | Out-Null
    Copy-Item -LiteralPath $binary -Destination (Join-Path $content 'agent/GoldSrcOps.GameEventAgent')
    $manifest = [ordered]@{
        schemaVersion = 1; purpose = 'game-event-agent'; version = $Version
        sourceRevision = $revision; targetRuntime = 'linux-x64'; queueSchemaVersion = 2
        payload = @([ordered]@{ path = 'agent/GoldSrcOps.GameEventAgent'; mode = '0750'
            length = (Get-Item -LiteralPath $binary).Length
            sha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    [IO.File]::WriteAllText((Join-Path $content 'manifest.json'), (($manifest | ConvertTo-Json -Depth 8) + "`n").Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    [IO.Compression.ZipFile]::CreateFromDirectory($content, $archive)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$archive.sha256", "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ Path = $archive; Sha256 = $hash; SourceRevision = $revision; Version = $Version }
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ($resolved.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
