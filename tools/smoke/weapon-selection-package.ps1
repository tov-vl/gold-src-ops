#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CacheDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [ValidateSet("sandbox", "game-host-addon")]
    [string]$Target = "sandbox",

    [switch]$EnablePlayerLookup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$builder = Join-Path $PSScriptRoot "../release/build-weapon-selection-sandbox.ps1"
$package = & $builder -CacheDirectory $CacheDirectory -OutputDirectory $OutputDirectory -Target $Target -EnablePlayerLookup:$EnablePlayerLookup
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

if ($addon) {
    $expectedConfiguration = 'goldsrcops_weapons_enabled "1"' + "`n"
    if ($EnablePlayerLookup) { $expectedConfiguration += 'goldsrcops_player_lookup_enabled "1"' + "`n" }
    if ($configuration -cne $expectedConfiguration) { throw "Unexpected lookup activation configuration." }
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

$invalidOutput = $OutputDirectory + "-invalid-sandbox"
$invalidRefused = $false
try {
    & $builder -CacheDirectory $CacheDirectory -OutputDirectory $invalidOutput -Target sandbox -EnablePlayerLookup | Out-Null
}
catch {
    if ($_.Exception.Message -cne "Player lookup activation requires a game-host-addon package.") { throw }
    $invalidRefused = $true
}
if (-not $invalidRefused -or (Test-Path -LiteralPath $invalidOutput)) {
    throw "Sandbox activation was not refused before creating output."
}

# Exercise the actual dictionary gate against byte boundaries and malformed input.
# Only this newly created sibling of content is temporary; the package is immutable.
$dictionaryProbe = Join-Path $OutputDirectory 'dictionary-probe.txt'
$dictionaryGate = Join-Path $PSScriptRoot 'player-menu-dictionary.ps1'
$dictionaryText = [IO.File]::ReadAllText((Join-Path $package.Content 'cstrike/addons/amxmodx/data/lang/goldsrcops-player-menu.txt'))
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$cases = @(
    @{ Text = ('a' * 31); Accepted = $true },
    @{ Text = (('я' * 15) + 'a'); Accepted = $true },
    @{ Text = ('a' * 32); Accepted = $false },
    @{ Text = ('я' * 16); Accepted = $false },
    @{ Text = ' '; Accepted = $false },
    @{ Text = "a`t"; Accepted = $false },
    @{ Text = "a$([char]0)b"; Accepted = $false },
    @{ Text = '%s'; Accepted = $false }
)
$checks = 0
try {
    foreach ($lang in @('en', 'ru')) {
        foreach ($key in @('GS_RANKS_TITLE', 'GS_PROFILE_TITLE', 'GS_TOPALL_TITLE')) {
            foreach ($case in $cases) {
                $pattern = "(?ms)(\[$lang\]\r?\n(?:(?!\[ru\]|\[en\]).)*?^$key = )[^\r\n]*"
                $probe = [regex]::Replace($dictionaryText, $pattern, { param($match) $match.Groups[1].Value + $case.Text })
                if ($probe -ceq $dictionaryText) { throw "Dictionary probe did not replace its target." }
                [IO.File]::WriteAllText($dictionaryProbe, $probe, $utf8)
                $accepted = $true
                try { & $dictionaryGate -DictionaryPath $dictionaryProbe | Out-Null }
                catch { $accepted = $false }
                if ($accepted -ne $case.Accepted) { throw "Unexpected dictionary boundary result: $lang/$key" }
                $checks++
            }
        }
    }
    foreach ($invalid in @([byte[]](0xd0), [byte[]](0xc0, 0xaf), [byte[]](0xed, 0xa0, 0x80))) {
        [IO.File]::WriteAllBytes($dictionaryProbe, ($utf8.GetBytes($dictionaryText) + $invalid))
        $rejected = $false
        try { & $dictionaryGate -DictionaryPath $dictionaryProbe | Out-Null }
        catch {
            if ($_.Exception.ToString() -notmatch 'DecoderFallbackException') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "Malformed UTF-8 was accepted." }
        $checks++
    }
}
finally {
    if (Test-Path -LiteralPath $dictionaryProbe) { Remove-Item -LiteralPath $dictionaryProbe }
}
Write-Output "MOTD_TITLE_DICTIONARY_SMOKE=passed cases=$checks captions=3 languages=2"
Write-Output "WEAPON_SELECTION_PACKAGE_SMOKE=passed"
$package
