#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CacheDirectory,
    [switch]$RuntimeFixture,
    [switch]$ProviderFixture,
    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
& (Join-Path $PSScriptRoot "amxx-game-event-producer.ps1") -CacheDirectory $CacheDirectory -Offline:$Offline | Out-Null
if (-not $IsWindows) { throw "Map-menu compiler smoke supports Windows only." }

$compiler = Join-Path $CacheDirectory "amxx-1.10.0.5481/addons/amxmodx/scripting/amxxpc.exe"
$includes = Join-Path $CacheDirectory "amxx-1.10.0.5481/addons/amxmodx/scripting/include"
$reApiIncludes = Join-Path $CacheDirectory "reapi-5.24.0.300/addons/amxmodx/scripting/include"
$productIncludes = Join-Path $root "samples/amxmodx-map-menu"
$source = Join-Path $productIncludes "goldsrcops_map_menu.sma"
$output = Join-Path $CacheDirectory "output/goldsrcops_map_menu.amxx"
if ($RuntimeFixture -and $ProviderFixture) { throw "Choose one fixture." }
if ($RuntimeFixture) {
    $source = Join-Path $PSScriptRoot "amxx-map-menu.sma"
    $output = Join-Path $CacheDirectory "output/map_menu_smoke.amxx"
}
if ($ProviderFixture) {
    $source = Join-Path $PSScriptRoot "amxx-map-language-provider.sma"
    $output = Join-Path $CacheDirectory "output/map_language_provider_smoke.amxx"
}
& (Join-Path $PSScriptRoot "player-menu-dictionary.ps1") -DictionaryPath (Join-Path $productIncludes 'goldsrcops-map-menu.txt') -SourcePath (Join-Path $productIncludes 'goldsrcops_map_menu.sma') | Out-Null
& $compiler $source "-i$includes" "-i$reApiIncludes" "-i$productIncludes" "-i$(Join-Path $root 'samples/amxmodx-weapon-selection')" "-o$output" -E
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output -PathType Leaf)) {
    throw "Map-menu compilation failed."
}
[pscustomobject]@{
    AmxxVersion = "1.10.0.5481"
    ReApiVersion = "5.24.0.300"
    Output = $output
    OutputSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
}
