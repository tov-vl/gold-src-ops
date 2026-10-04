#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CacheDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [ValidateSet("sandbox", "game-host-addon")]
    [string]$Target = "sandbox"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$builder = Join-Path $PSScriptRoot "../release/build-weapon-selection-sandbox.ps1"
$package = & $builder -CacheDirectory $CacheDirectory -OutputDirectory $OutputDirectory -Target $Target
$archiveHash = (Get-FileHash -LiteralPath $package.Archive -Algorithm SHA256).Hash
$manifest = Get-Content -LiteralPath (Join-Path $package.Content "manifest.json") -Raw | ConvertFrom-Json
$addon = $Target -eq "game-host-addon"
$purpose = if ($addon) { "game-host-addon" } else { "local-sandbox" }
if ($manifest.schemaVersion -ne 2 -or $manifest.purpose -cne $purpose `
    -or $manifest.productionInstallSupported -ne $addon -or $manifest.enabledByDefault -ne $addon `
    -or $manifest.amxxVersion -cne "1.10.0.5481" -or $manifest.reApiVersion -cne "5.24.0.300") {
    throw "Unexpected sandbox manifest boundary."
}

$expectedPayload = @(
    "cstrike/addons/amxmodx/configs/plugins/goldsrcops-weapon-selection.cfg",
    "cstrike/addons/amxmodx/plugins/goldsrcops_weapon_selection.amxx"
)
if ($addon) { $expectedPayload += "cstrike/addons/amxmodx/configs/plugins-goldsrcops-weapons.ini" }
$expectedPayload += "cstrike/addons/amxmodx/data/lang/goldsrcops-player-menu.txt"
if (@($manifest.payload).Count -ne $expectedPayload.Count -or
    (Compare-Object $expectedPayload @($manifest.payload.path))) {
    throw "Unexpected sandbox payload."
}

$zip = [IO.Compression.ZipFile]::OpenRead($package.Archive)
try {
    $expectedEntries = @($expectedPayload) + "manifest.json"
    if ($zip.Entries.Count -ne $expectedEntries.Count -or
        (Compare-Object $expectedEntries @($zip.Entries.FullName))) {
        throw "Archive contains unexpected files or directories."
    }
    foreach ($file in $manifest.payload) {
        $stream = $zip.GetEntry($file.path).Open()
        try {
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
            if ($hash -cne $file.sha256) {
                throw "Archive payload hash mismatch."
            }
        }
        finally { $stream.Dispose() }
    }
}
finally { $zip.Dispose() }

$configuration = [IO.File]::ReadAllText((Join-Path $package.Content $expectedPayload[0]))
$expectedSwitch = if ($addon) { 1 } else { 0 }
if ($configuration.Contains("`r") -or $configuration -notmatch "(?m)^goldsrcops_weapons_enabled `"$expectedSwitch`"$") {
    throw "Configuration has an unexpected switch value or line endings."
}
if ($addon -and [IO.File]::ReadAllText((Join-Path $package.Content $expectedPayload[2])) -cne "goldsrcops_weapon_selection.amxx`n") {
    throw "Supplemental loader is not the exact one-plugin list."
}

$refused = $false
try {
    & $builder -CacheDirectory $CacheDirectory -OutputDirectory $OutputDirectory -Target $Target | Out-Null
}
catch {
    if ($_.Exception.Message -cne "Sandbox output already exists; choose a new directory.") { throw }
    $refused = $true
}
if (-not $refused -or (Get-FileHash -LiteralPath $package.Archive -Algorithm SHA256).Hash -cne $archiveHash) {
    throw "Existing sandbox output was not preserved."
}

Write-Output "WEAPON_SELECTION_PACKAGE_SMOKE=passed"
$package
