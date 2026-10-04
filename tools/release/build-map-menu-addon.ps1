#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$CacheDirectory,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Addon output already exists; choose a new directory."
}
$results = @(& (Join-Path $root "tools/smoke/amxx-map-menu.ps1") -CacheDirectory $CacheDirectory -Offline:$Offline)
$builds = @($results | Where-Object { $_.PSObject.Properties.Name -contains "OutputSha256" })
if ($builds.Count -ne 1) { throw "Expected exactly one compiled product plugin." }
$build = $builds[0]
if ((Get-FileHash -LiteralPath $build.Output).Hash.ToLowerInvariant() -cne $build.OutputSha256) {
    throw "Compiled plugin changed before packaging."
}
$source = Join-Path $root "samples/amxmodx-map-menu/goldsrcops_map_menu.sma"
$defaultConfig = [IO.File]::ReadAllText((Join-Path $root "samples/amxmodx-map-menu/goldsrcops-map-menu.cfg")).Trim()
if ($defaultConfig -cne 'goldsrcops_maps_enabled "0"') { throw "Unexpected default configuration." }
$plugin = "cstrike/addons/amxmodx/plugins/goldsrcops_map_menu.amxx"
$config = "cstrike/addons/amxmodx/configs/plugins/goldsrcops-map-menu.cfg"
$loader = "cstrike/addons/amxmodx/configs/plugins-goldsrcops-maps.ini"
$content = Join-Path $OutputDirectory "content"
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
foreach ($path in @($plugin, $config, $loader)) {
    New-Item -ItemType Directory -Force -Path (Split-Path (Join-Path $content $path)) | Out-Null
}
Copy-Item -LiteralPath $build.Output -Destination (Join-Path $content $plugin)
[IO.File]::WriteAllText((Join-Path $content $config), "goldsrcops_maps_enabled `"1`"`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $content $loader), "goldsrcops_map_menu.amxx`n", [Text.UTF8Encoding]::new($false))
$payload = @(@($plugin, $config, $loader) | ForEach-Object {
    [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $content $_)).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{
    schemaVersion = 1
    purpose = "map-menu-addon"
    productionInstallSupported = $true
    enabledByDefault = $true
    amxxVersion = $build.AmxxVersion
    reApiVersion = $build.ReApiVersion
    sourceSha256 = (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
    payload = $payload
}
$manifestPath = Join-Path $content "manifest.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6) + "`n", [Text.UTF8Encoding]::new($false))
$archivePath = Join-Path $OutputDirectory "map-menu-addon.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($content, $archivePath)
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $expected = @($plugin, $config, $loader, "manifest.json") | Sort-Object
    $actual = @($archive.Entries | Where-Object Name | Select-Object -ExpandProperty FullName | Sort-Object)
    if (@(Compare-Object $expected $actual).Count) { throw "Unexpected archive payload." }
    foreach ($entry in $archive.Entries | Where-Object Name) {
        $stream = $entry.Open()
        try {
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
            if ($hash -cne (Get-FileHash -LiteralPath (Join-Path $content $entry.FullName)).Hash.ToLowerInvariant()) {
                throw "Archive payload hash mismatch."
            }
        } finally { $stream.Dispose() }
    }
} finally { $archive.Dispose() }
[pscustomobject]@{
    Archive = $archivePath
    ArchiveSha256 = (Get-FileHash -LiteralPath $archivePath).Hash.ToLowerInvariant()
    ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant()
    PluginSha256 = $build.OutputSha256
    Content = $content
}
