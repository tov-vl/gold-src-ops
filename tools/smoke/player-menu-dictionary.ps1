#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DictionaryPath,
    [string]$SourcePath = (Join-Path $PSScriptRoot '../../samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$bytes = [IO.File]::ReadAllBytes($DictionaryPath)
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xef -and $bytes[1] -eq 0xbb -and $bytes[2] -eq 0xbf) {
    throw "Dictionary must be UTF-8 without BOM."
}
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$text = $utf8.GetString($bytes)
if ($text -match '[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]') {
    throw "Dictionary contains a control character."
}
$sections = @{}
$language = $null
foreach ($line in ($text -split "\r?\n")) {
    if (-not $line.Trim()) { continue }
    if ($line -match '^\[(en|ru)\]$') {
        $language = $Matches[1]
        if ($sections.ContainsKey($language)) { throw "Duplicate language section." }
        $sections[$language] = @{}
    }
    elseif ($language -and $line -match '^(GS_[A-Z_]+) = (.+)$') {
        $key = $Matches[1]
        $value = $Matches[2]
        if ($sections[$language].ContainsKey($key)) { throw "Duplicate dictionary key: $key" }
        if ($value.Contains('^') -or $value.Contains('\')) { throw "Unsupported dictionary escape: $key" }
        $sections[$language][$key] = $value
    }
    else { throw "Invalid dictionary line." }
}
if ($sections.Count -ne 2 -or (Compare-Object @($sections.en.Keys) @($sections.ru.Keys))) {
    throw "English and Russian dictionary keys must match."
}
$source = [IO.File]::ReadAllText($SourcePath)
$used = @([regex]::Matches($source, '"(GS_[A-Z_]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if (Compare-Object $used @($sections.en.Keys)) { throw "Dictionary keys do not match product references." }
# Full HTML headings and client window captions have different byte budgets.
foreach ($lang in @('en', 'ru')) {
    foreach ($key in @('GS_RANKS_TITLE', 'GS_PROFILE_TITLE', 'GS_TOPALL_TITLE')) {
        if ($key -notin $used) { continue }
        $caption = $sections[$lang][$key]
        if ([string]::IsNullOrWhiteSpace($caption) -or $caption -match '[%\t]' -or
            $utf8.GetByteCount($caption) -gt 31) {
            throw "MOTD caption must be plain UTF-8 text of 1-31 bytes: $lang/$key"
        }
    }
    foreach ($key in @('GS_RANKS', 'GS_PROFILE', 'GS_TOPALL')) {
        if ($key -notin $used) { continue }
        if ($utf8.GetByteCount($sections[$lang][$key]) -gt 63) {
            throw "MOTD heading exceeds its byte budget: $lang/$key"
        }
    }
}
# Selected weapon text is inserted inside brackets and menu color codes.
foreach ($lang in @('en', 'ru')) {
    $selected = $sections[$lang]['GS_WEAPON_SELECTED']
    if ([string]::IsNullOrWhiteSpace($selected) -or $selected -match '[%\t\[\]]' -or
        $utf8.GetByteCount($selected) -gt 24) {
        throw "Weapon marker must be plain UTF-8 text of 1-24 bytes: $lang"
    }
    foreach ($title in @('GS_PRIMARY', 'GS_PISTOL')) {
        if ($utf8.GetByteCount($sections[$lang][$title]) -gt 127) { throw "Weapon menu title exceeds its byte budget: $lang/$title" }
    }
    $longest = 'Desert Eagle \y[' + $selected + ']\w'
    if ($utf8.GetByteCount($longest) -gt 63) { throw "Weapon item exceeds its byte budget: $lang" }
}
foreach ($key in $sections.en.Keys) {
    $formats = @{}
    foreach ($lang in @('en', 'ru')) {
        $value = $sections[$lang][$key]
        $formats[$lang] = @([regex]::Matches($value, '%(?:\.2f|d|s)') | ForEach-Object Value)
        if ([regex]::Replace($value, '%(?:\.2f|d|s)', '').Contains('%')) { throw "Unsupported format: $key" }
        # Ratio text has its own 13-byte bound; names/ranks retain 31 bytes.
        $worst = $value
        if ($key -in @('GS_SAVED_STATS', 'GS_MAP_STATS', 'GS_PROFILE_SCORE', 'GS_TOP_ROW')) {
            $expected = if ($key -eq 'GS_TOP_ROW') { '%d,%s,%d,%d,%s' } else { '%d,%d,%s' }
            if (($formats[$lang] -join ',') -cne $expected) { throw "K/D format mismatch: $lang/$key" }
            $position = $worst.LastIndexOf('%s', [StringComparison]::Ordinal)
            $worst = $worst.Remove($position, 2).Insert($position, '1000000000.00')
        }
        $worst = $worst.Replace('%s', ('x' * 31)).Replace('%d', '1000000000').Replace('%.2f', '1000000000.00')
        if ($utf8.GetByteCount('[GoldSrcOps] ' + $worst) -gt 190) { throw "Dictionary message exceeds chat limit: $lang/$key" }
    }
    if (($formats.en -join ',') -cne ($formats.ru -join ',')) { throw "Translation format mismatch: $key" }
}
Write-Output "PLAYER_MENU_DICTIONARY_SMOKE=passed keys=$($sections.en.Count) languages=2 utf8=strict"
