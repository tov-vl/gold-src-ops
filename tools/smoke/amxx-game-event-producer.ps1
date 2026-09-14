#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$CacheDirectory,
    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
if ([string]::IsNullOrWhiteSpace($CacheDirectory)) {
    $CacheDirectory = Join-Path $repositoryRoot "artifacts/amxx-game-event-producer"
}

$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
$sourcePath = Join-Path $repositoryRoot "samples/amxmodx-game-event-producer/goldsrcops_game_events.sma"
$amxxVersion = "1.10.0.5481"
$reApiVersion = "5.24.0.300"
$reApiArchiveName = "reapi-bin-$reApiVersion.zip"
$reApiSha256 = "16114cf5a782e9d3d0c9443c23cd937c17cc09dc9b16ff998ce48bda5faf0a81"

if ($IsWindows) {
    $amxxArchiveName = "amxmodx-1.10.0-git5481-base-windows.zip"
    $amxxSha256 = "5ece16f1c0060de0591cdc7ea3643c71598b3754938c7d39e3b7923dafbc57af"
    $compilerRelativePath = "addons/amxmodx/scripting/amxxpc.exe"
}
elseif ($IsLinux) {
    $amxxArchiveName = "amxmodx-1.10.0-git5481-base-linux.tar.gz"
    $amxxSha256 = "ee33b31ae92afd94802c43eae14ecdfa1ffa2ba0b11658e8bec98f48a5881272"
    $compilerRelativePath = "addons/amxmodx/scripting/amxxpc"
}
else {
    throw "The sandbox compiler smoke supports Windows and Linux only."
}

function Get-VerifiedAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Destination -PathType Leaf)) {
        if ($Offline) {
            throw "Required offline asset is missing: $(Split-Path $Destination -Leaf)."
        }

        Invoke-WebRequest -Uri $Uri -OutFile $Destination -MaximumRedirection 5
    }

    $actualSha256 = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -cne $ExpectedSha256) {
        throw "Asset digest mismatch for $(Split-Path $Destination -Leaf)."
    }
}

New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null

$amxxArchivePath = Join-Path $CacheDirectory $amxxArchiveName
$reApiArchivePath = Join-Path $CacheDirectory $reApiArchiveName
$amxxReleaseUri = "https://github.com/alliedmodders/amxmodx/releases/download/$amxxVersion/$amxxArchiveName"
$reApiReleaseUri = "https://github.com/rehlds/ReAPI/releases/download/$reApiVersion/$reApiArchiveName"

Get-VerifiedAsset -Uri $amxxReleaseUri -Destination $amxxArchivePath -ExpectedSha256 $amxxSha256
Get-VerifiedAsset -Uri $reApiReleaseUri -Destination $reApiArchivePath -ExpectedSha256 $reApiSha256

$amxxRoot = Join-Path $CacheDirectory "amxx-$amxxVersion"
$reApiRoot = Join-Path $CacheDirectory "reapi-$reApiVersion"
New-Item -ItemType Directory -Force -Path $amxxRoot, $reApiRoot | Out-Null

if ($IsWindows) {
    Expand-Archive -LiteralPath $amxxArchivePath -DestinationPath $amxxRoot -Force
}
else {
    & tar -xzf $amxxArchivePath -C $amxxRoot
    if ($LASTEXITCODE -ne 0) {
        throw "AMX Mod X archive extraction failed with exit code $LASTEXITCODE."
    }
}

Expand-Archive -LiteralPath $reApiArchivePath -DestinationPath $reApiRoot -Force

$compilerPath = Join-Path $amxxRoot $compilerRelativePath
$amxxIncludePath = Join-Path $amxxRoot "addons/amxmodx/scripting/include"
$reApiIncludePath = Join-Path $reApiRoot "addons/amxmodx/scripting/include"
$outputDirectory = Join-Path $CacheDirectory "output"
$outputPath = Join-Path $outputDirectory "goldsrcops_game_events.amxx"

foreach ($requiredPath in $compilerPath, $amxxIncludePath, $reApiIncludePath, $sourcePath) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required compiler input is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue

& $compilerPath `
    $sourcePath `
    "-i$amxxIncludePath" `
    "-i$reApiIncludePath" `
    "-o$outputPath" `
    -E

if ($LASTEXITCODE -ne 0) {
    throw "AMX Mod X producer compilation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw "AMX Mod X compiler did not create the expected output."
}

[pscustomobject]@{
    AmxxVersion = $amxxVersion
    ReApiVersion = $reApiVersion
    Output = $outputPath
    OutputSha256 = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
