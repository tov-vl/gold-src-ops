#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$CacheDirectory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [ValidateSet("sandbox", "game-host-addon")]
    [string]$Target = "sandbox"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Sandbox output already exists; choose a new directory."
}

$buildResults = @(& (Join-Path $root "tools/smoke/amxx-weapon-selection.ps1") `
    -CacheDirectory $CacheDirectory)
$build = @($buildResults | Where-Object { $_.PSObject.Properties.Name -contains "OutputSha256" })
if ($build.Count -ne 1) {
    throw "Compiler did not return exactly one verified plugin."
}
$build = $build[0]
if ((Get-FileHash -LiteralPath $build.Output -Algorithm SHA256).Hash.ToLowerInvariant() -cne $build.OutputSha256) {
    throw "Compiled plugin changed before packaging."
}

$source = Join-Path $root "samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma"
$configuration = Join-Path $root "samples/amxmodx-weapon-selection/goldsrcops-weapon-selection.cfg"
$dictionary = Join-Path $root "samples/amxmodx-weapon-selection/goldsrcops-player-menu.txt"
& (Join-Path $root "tools/smoke/player-menu-dictionary.ps1") -DictionaryPath $dictionary | Out-Null
$configText = [IO.File]::ReadAllText($configuration).Replace("`r`n", "`n")
$configCommands = @($configText.Split("`n") | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith("//") })
if ($configCommands.Count -ne 1 -or $configCommands[0] -cne 'goldsrcops_weapons_enabled "0"') {
    throw "Sandbox configuration must contain only the default-off command."
}

# Каталог создаётся один раз; существующий результат не заменяется и не очищается.
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$content = Join-Path $OutputDirectory "content"
$pluginRelative = "cstrike/addons/amxmodx/plugins/goldsrcops_weapon_selection.amxx"
$configRelative = "cstrike/addons/amxmodx/configs/plugins/goldsrcops-weapon-selection.cfg"
$loaderRelative = "cstrike/addons/amxmodx/configs/plugins-goldsrcops-weapons.ini"
$dictionaryRelative = "cstrike/addons/amxmodx/data/lang/goldsrcops-player-menu.txt"
$relativePaths = @($pluginRelative, $configRelative, $dictionaryRelative)
if ($Target -eq "game-host-addon") {
    $relativePaths += $loaderRelative
    $configText = 'goldsrcops_weapons_enabled "1"' + "`n"
}
foreach ($relative in $relativePaths) {
    New-Item -ItemType Directory -Force -Path (Split-Path (Join-Path $content $relative)) | Out-Null
}
Copy-Item -LiteralPath $build.Output -Destination (Join-Path $content $pluginRelative)
Copy-Item -LiteralPath $dictionary -Destination (Join-Path $content $dictionaryRelative)
[IO.File]::WriteAllText((Join-Path $content $configRelative), $configText, [Text.UTF8Encoding]::new($false))
if ($Target -eq "game-host-addon") {
    [IO.File]::WriteAllText((Join-Path $content $loaderRelative), "goldsrcops_weapon_selection.amxx`n", [Text.UTF8Encoding]::new($false))
}

$payload = @($relativePaths | ForEach-Object {
    [ordered]@{
        path = $_
        sha256 = (Get-FileHash -LiteralPath (Join-Path $content $_) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{
    schemaVersion = 2
    purpose = $(if ($Target -eq "sandbox") { "local-sandbox" } else { "game-host-addon" })
    productionInstallSupported = ($Target -eq "game-host-addon")
    enabledByDefault = ($Target -eq "game-host-addon")
    amxxVersion = $build.AmxxVersion
    reApiVersion = $build.ReApiVersion
    sourceSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    payload = $payload
}
[IO.File]::WriteAllText((Join-Path $content "manifest.json"),
    ($manifest | ConvertTo-Json -Depth 6) + "`n", [Text.UTF8Encoding]::new($false))

$archive = Join-Path $OutputDirectory "weapon-selection-$Target.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($content, $archive)
[pscustomobject]@{
    Archive = $archive
    ArchiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Content = $content
    PluginSha256 = $build.OutputSha256
}
