#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CacheDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$builder = Join-Path $PSScriptRoot "../release/build-weapon-selection-sandbox.ps1"
$package = & $builder -CacheDirectory $CacheDirectory -OutputDirectory $OutputDirectory
$archiveHash = (Get-FileHash -LiteralPath $package.Archive -Algorithm SHA256).Hash
$manifest = Get-Content -LiteralPath (Join-Path $package.Content "manifest.json") -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.purpose -cne "local-sandbox" `
    -or $manifest.productionInstallSupported -ne $false -or $manifest.enabledByDefault -ne $false `
    -or $manifest.amxxVersion -cne "1.10.0.5481" -or $manifest.reApiVersion -cne "5.24.0.300") {
    throw "Unexpected sandbox manifest boundary."
}

$expectedPayload = @(
    "cstrike/addons/amxmodx/configs/goldsrcops-weapon-selection.cfg",
    "cstrike/addons/amxmodx/plugins/goldsrcops_weapon_selection.amxx"
)
if (@($manifest.payload).Count -ne 2 -or
    (Compare-Object $expectedPayload @($manifest.payload.path))) {
    throw "Unexpected sandbox payload."
}

$zip = [IO.Compression.ZipFile]::OpenRead($package.Archive)
try {
    $expectedEntries = @($expectedPayload) + "manifest.json"
    if ($zip.Entries.Count -ne 3 -or
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
if ($configuration.Contains("`r") -or $configuration -notmatch '(?m)^goldsrcops_weapons_enabled "0"$') {
    throw "Configuration is not default-off LF text."
}

$refused = $false
try {
    & $builder -CacheDirectory $CacheDirectory -OutputDirectory $OutputDirectory | Out-Null
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
