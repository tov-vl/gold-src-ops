#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CacheDirectory,

    [switch]$RuntimeFixture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
# Существующий проверенный runner сверяет архивы и распаковывает закреплённый toolchain.
& (Join-Path $PSScriptRoot "amxx-game-event-producer.ps1") -CacheDirectory $CacheDirectory -Offline | Out-Null

if (-not $IsWindows) {
    throw "The initial weapon-selection compiler smoke supports Windows only."
}

$compiler = Join-Path $CacheDirectory "amxx-1.10.0.5481/addons/amxmodx/scripting/amxxpc.exe"
$amxxIncludes = Join-Path $CacheDirectory "amxx-1.10.0.5481/addons/amxmodx/scripting/include"
$reApiIncludes = Join-Path $CacheDirectory "reapi-5.24.0.300/addons/amxmodx/scripting/include"
$source = Join-Path $root "samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma"
$output = Join-Path $CacheDirectory "output/goldsrcops_weapon_selection.amxx"
if ($RuntimeFixture) {
    $source = Join-Path $PSScriptRoot "amxx-spawn-loadout.sma"
    $output = Join-Path $CacheDirectory "output/spawn_loadout_smoke.amxx"
}
$productIncludes = Join-Path $root "samples/amxmodx-weapon-selection"

& $compiler $source "-i$amxxIncludes" "-i$reApiIncludes" "-i$productIncludes" "-o$output" -E
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output -PathType Leaf)) {
    throw "Weapon-selection compilation failed."
}

[pscustomobject]@{
    AmxxVersion = "1.10.0.5481"
    ReApiVersion = "5.24.0.300"
    Output = $output
    OutputSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
}
