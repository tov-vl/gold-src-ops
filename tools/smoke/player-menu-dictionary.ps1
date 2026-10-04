#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$DictionaryPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$bytes = [IO.File]::ReadAllBytes($DictionaryPath)
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xef -and $bytes[1] -eq 0xbb -and $bytes[2] -eq 0xbf) {
    throw "Dictionary must be UTF-8 without BOM."
}
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$text = $utf8.GetString($bytes)
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
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma'))
$used = @([regex]::Matches($source, '"(GS_[A-Z_]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if (Compare-Object $used @($sections.en.Keys)) { throw "Dictionary keys do not match product references." }
foreach ($key in $sections.en.Keys) {
    $formats = @{}
    foreach ($lang in @('en', 'ru')) {
        $value = $sections[$lang][$key]
        $formats[$lang] = @([regex]::Matches($value, '%(?:\.2f|d|s)') | ForEach-Object Value)
        if ([regex]::Replace($value, '%(?:\.2f|d|s)', '').Contains('%')) { throw "Unsupported format: $key" }
        # Worst-case chat values: bounded nickname/rank, counter and finite K/D.
        $worst = $value.Replace('%s', ('x' * 31)).Replace('%d', '1000000000').Replace('%.2f', '1000000000.00')
        if ($utf8.GetByteCount('[GoldSrcOps] ' + $worst) -gt 190) { throw "Dictionary message exceeds chat limit: $lang/$key" }
    }
    if (($formats.en -join ',') -cne ($formats.ru -join ',')) { throw "Translation format mismatch: $key" }
}
Write-Output "PLAYER_MENU_DICTIONARY_SMOKE=passed keys=$($sections.en.Count) languages=2"
