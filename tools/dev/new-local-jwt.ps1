#Requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Name = "local-operator",

    [ValidateSet("Reader", "Operator")]
    [string]$Role = "Operator",

    [ValidatePattern("^[1-9][0-9]*[dhms]$")]
    [string]$ValidFor = "1d"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$apiProject = Join-Path $repoRoot "src\GoldSrcOps.Api"
$localSettings = Join-Path $apiProject "appsettings.Local.json"
$localDotnet = Join-Path $repoRoot ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { "dotnet" }

if (-not (Test-Path -LiteralPath $localSettings)) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        $localSettings,
        "{}" + [Environment]::NewLine,
        $utf8NoBom)
}

& $dotnet user-jwts create `
    --project $apiProject `
    --appsettings-file $localSettings `
    --name $Name `
    --role $Role `
    --valid-for $ValidFor

if ($LASTEXITCODE -ne 0) {
    throw "Development JWT creation failed with exit code $LASTEXITCODE."
}
