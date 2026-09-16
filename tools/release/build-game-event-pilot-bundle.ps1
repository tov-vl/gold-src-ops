#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$BundleVersion = "2.11.0-pilot.1",

    [string]$OutputDirectory,

    [string]$CacheDirectory,

    [string]$SourceRevision,

    [switch]$Offline,

    [switch]$Plan,

    [switch]$DevelopmentBuild,

    [switch]$ReplaceDevelopmentOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/game-event-pilot/output"
}
if ([string]::IsNullOrWhiteSpace($CacheDirectory)) {
    $CacheDirectory = Join-Path $repositoryRoot "artifacts/game-event-pilot"
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
$agentProject = Join-Path $repositoryRoot "src/GoldSrcOps.GameEventAgent/GoldSrcOps.GameEventAgent.csproj"
$agentSettings = Join-Path $repositoryRoot "src/GoldSrcOps.GameEventAgent/appsettings.json"
$producerSource = Join-Path $repositoryRoot "samples/amxmodx-game-event-producer/goldsrcops_game_events.sma"
$producerCompiler = Join-Path $repositoryRoot "tools/smoke/amxx-game-event-producer.ps1"

$amxxVersion = "1.10.0.5481"
$amxxBaseArchive = "amxmodx-1.10.0-git5481-base-linux.tar.gz"
$amxxBaseSha256 = "ee33b31ae92afd94802c43eae14ecdfa1ffa2ba0b11658e8bec98f48a5881272"
$amxxCstrikeArchive = "amxmodx-1.10.0-git5481-cstrike-linux.tar.gz"
$amxxCstrikeSha256 = "76ff2bdd39f6dc14a2088ff4af724599c69a8ae7e3526679927aaef4ca898bf2"
$metamodVersion = "1.3.0.149"
$metamodArchive = "metamod-bin-$metamodVersion.zip"
$metamodSha256 = "ede7f59c4e0220afe8c02aa348a130cce527f87d36ffdb674e37a501ce57be94"
$reApiVersion = "5.24.0.300"
$reApiArchive = "reapi-bin-$reApiVersion.zip"
$reApiSha256 = "16114cf5a782e9d3d0c9443c23cd937c17cc09dc9b16ff998ce48bda5faf0a81"

$assets = @(
    [pscustomobject]@{
        Name = "AMX Mod X base"
        Version = $amxxVersion
        ArchiveName = $amxxBaseArchive
        Uri = "https://github.com/alliedmodders/amxmodx/releases/download/$amxxVersion/$amxxBaseArchive"
        Sha256 = $amxxBaseSha256
        Format = "tar.gz"
    },
    [pscustomobject]@{
        Name = "AMX Mod X Counter-Strike"
        Version = $amxxVersion
        ArchiveName = $amxxCstrikeArchive
        Uri = "https://github.com/alliedmodders/amxmodx/releases/download/$amxxVersion/$amxxCstrikeArchive"
        Sha256 = $amxxCstrikeSha256
        Format = "tar.gz"
    },
    [pscustomobject]@{
        Name = "Metamod-R"
        Version = $metamodVersion
        ArchiveName = $metamodArchive
        Uri = "https://github.com/rehlds/Metamod-R/releases/download/$metamodVersion/$metamodArchive"
        Sha256 = $metamodSha256
        Format = "zip"
    },
    [pscustomobject]@{
        Name = "ReAPI"
        Version = $reApiVersion
        ArchiveName = $reApiArchive
        Uri = "https://github.com/rehlds/ReAPI/releases/download/$reApiVersion/$reApiArchive"
        Sha256 = $reApiSha256
        Format = "zip"
    }
)

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw $Message
}

function Write-Utf8Lf {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $normalized = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
    [IO.File]::WriteAllText($Path, $normalized, [Text.UTF8Encoding]::new($false))
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        Fail "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

function Get-LowerSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-SafeRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $normalized = $Path.TrimEnd("/")
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        $normalized.StartsWith("/", [StringComparison]::Ordinal) -or
        $normalized.Contains("\", [StringComparison]::Ordinal) -or
        $normalized -match '^[A-Za-z]:' -or
        $normalized -match '[\x00-\x1f]' -or
        $normalized.Split("/", [StringSplitOptions]::RemoveEmptyEntries) -contains "..") {
        Fail "An archive contains an unsafe or unsupported path."
    }
}

function Assert-SafePayloadPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-SafeRelativePath $Path
    if ($Path -notmatch '^[A-Za-z0-9._/-]+$') {
        Fail "The assembled payload contains an unsupported path."
    }
}

function Get-VerifiedAsset {
    param([Parameter(Mandatory = $true)]$Asset)

    $assetDirectory = Join-Path $CacheDirectory "assets"
    $destination = Join-Path $assetDirectory $Asset.ArchiveName
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
        if ($Offline) {
            Fail "Required offline asset is missing: $($Asset.ArchiveName)."
        }

        Invoke-WebRequest `
            -Uri $Asset.Uri `
            -OutFile $destination `
            -MaximumRedirection 5
    }

    $actualSha256 = Get-LowerSha256 $destination
    if ($actualSha256 -cne $Asset.Sha256) {
        Fail "Asset digest mismatch for $($Asset.ArchiveName)."
    }

    return $destination
}

function Expand-SafeTarArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Archive,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $entries = & tar -tzf $Archive
    if ($LASTEXITCODE -ne 0) {
        Fail "The pinned tar archive cannot be listed."
    }
    foreach ($entry in $entries) {
        Assert-SafeRelativePath $entry
    }

    Invoke-NativeCommand `
        -FilePath "tar" `
        -Arguments @("-xzf", $Archive, "-C", $Destination) `
        -FailureMessage "The pinned tar archive could not be extracted."
}

function Expand-SafeZipArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Archive,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            Assert-SafeRelativePath $entry.FullName
            $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixMode -eq 0xA000) {
                Fail "A pinned ZIP archive contains a symbolic link."
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Destination, $true)
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        Fail "A required pinned component file is missing: $(Split-Path $Source -Leaf)."
    }

    $parent = Split-Path $Destination -Parent
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination
}

function Get-RepositoryRevision {
    $result = @(& git -C $repositoryRoot rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $result.Count -ne 1) {
        Fail "The repository source revision could not be resolved."
    }

    return $result[0].Trim().ToLowerInvariant()
}

function Test-TrackedSourceDirty {
    $result = & git -C $repositoryRoot status --porcelain --untracked-files=no
    if ($LASTEXITCODE -ne 0) {
        Fail "The tracked source state could not be inspected."
    }

    return @($result).Count -gt 0
}

function Assert-Inputs {
    if ($BundleVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$') {
        Fail "BundleVersion must be a SemVer version without build metadata."
    }
    if ($SourceRevision -notmatch '^[0-9a-f]{40}$') {
        Fail "SourceRevision must be a lowercase 40-character Git SHA."
    }
    if ($ReplaceDevelopmentOutput -and -not $DevelopmentBuild) {
        Fail "ReplaceDevelopmentOutput is allowed only with DevelopmentBuild."
    }
}

function Write-Plan {
    Write-Output "PLAN: publish GoldSrcOps.GameEventAgent as self-contained linux-x64 single-file payload"
    Write-Output "PLAN: compile the default-off producer with AMX Mod X $amxxVersion and ReAPI $reApiVersion"
    Write-Output "PLAN: verify AMX Mod X Linux base SHA-256 $amxxBaseSha256"
    Write-Output "PLAN: verify AMX Mod X Linux Counter-Strike SHA-256 $amxxCstrikeSha256"
    Write-Output "PLAN: verify Metamod-R $metamodVersion SHA-256 $metamodSha256"
    Write-Output "PLAN: verify ReAPI $reApiVersion SHA-256 $reApiSha256"
    Write-Output "PLAN: emit one manifest with source revision, immutable file hashes, and all activation gates false"
    Write-Output "PLAN_ONLY: no artifacts were downloaded, compiled, or written"
}

if ([string]::IsNullOrWhiteSpace($SourceRevision)) {
    $SourceRevision = Get-RepositoryRevision
}
else {
    $SourceRevision = $SourceRevision.Trim().ToLowerInvariant()
}

Assert-Inputs
if ($Plan) {
    Write-Plan
    exit 0
}

$currentRevision = Get-RepositoryRevision
if ($SourceRevision -cne $currentRevision) {
    Fail "SourceRevision does not match the checked-out repository revision."
}

$sourceDirty = Test-TrackedSourceDirty
if ($sourceDirty -and -not $DevelopmentBuild) {
    Fail "Tracked source changes are present. Commit them or use DevelopmentBuild for local validation."
}
$productionEligible = -not $DevelopmentBuild -and -not $sourceDirty

foreach ($requiredPath in $agentProject, $agentSettings, $producerSource, $producerCompiler) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        Fail "Required bundle input is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $CacheDirectory "assets") | Out-Null

$shortRevision = $SourceRevision.Substring(0, 12)
$outputQualifier = if ($DevelopmentBuild) { "-development" } else { "" }
$bundleBaseName = "goldsrcops-game-event-pilot-$BundleVersion-$shortRevision$outputQualifier"
$bundlePath = Join-Path $OutputDirectory "$bundleBaseName.zip"
$digestPath = "$bundlePath.sha256"
if ((Test-Path -LiteralPath $bundlePath) -or (Test-Path -LiteralPath $digestPath)) {
    if (-not ($DevelopmentBuild -and $ReplaceDevelopmentOutput)) {
        Fail "Bundle output already exists; immutable outputs are not overwritten."
    }

    Remove-Item -LiteralPath $bundlePath, $digestPath -Force -ErrorAction SilentlyContinue
}

$temporaryRoot = Join-Path $OutputDirectory ".bundle-build-$PID-$([IO.Path]::GetRandomFileName())"
$stageRoot = Join-Path $temporaryRoot "stage"
$publishRoot = Join-Path $temporaryRoot "publish"
$extractRoot = Join-Path $temporaryRoot "extract"
$amxxRoot = Join-Path $extractRoot "amxx"
$metamodRoot = Join-Path $extractRoot "metamod"
$reApiRoot = Join-Path $extractRoot "reapi"

try {
    New-Item -ItemType Directory -Force -Path `
        $stageRoot, $publishRoot, $amxxRoot, $metamodRoot, $reApiRoot | Out-Null

    $assetPaths = @{}
    foreach ($asset in $assets) {
        $assetPaths[$asset.ArchiveName] = Get-VerifiedAsset $asset
    }

    Expand-SafeTarArchive -Archive $assetPaths[$amxxBaseArchive] -Destination $amxxRoot
    Expand-SafeTarArchive -Archive $assetPaths[$amxxCstrikeArchive] -Destination $amxxRoot
    Expand-SafeZipArchive -Archive $assetPaths[$metamodArchive] -Destination $metamodRoot
    Expand-SafeZipArchive -Archive $assetPaths[$reApiArchive] -Destination $reApiRoot

    Invoke-NativeCommand `
        -FilePath "dotnet" `
        -Arguments @(
            "publish",
            $agentProject,
            "--configuration", "Release",
            "--runtime", "linux-x64",
            "--self-contained", "true",
            "--output", $publishRoot,
            "-p:GameEventPilotBundle=true",
            "-p:ContinuousIntegrationBuild=true",
            "-p:Version=$BundleVersion",
            "-p:SourceRevisionId=$SourceRevision",
            "-p:RepositoryCommit=$SourceRevision"
        ) `
        -FailureMessage "The game-event agent publish failed."

    $publishedAgent = Join-Path $publishRoot "GoldSrcOps.GameEventAgent"
    if (-not (Test-Path -LiteralPath $publishedAgent -PathType Leaf)) {
        Fail "The self-contained agent executable was not published."
    }
    $unexpectedNativeFiles = @(Get-ChildItem -LiteralPath $publishRoot -File |
        Where-Object { $_.Extension -in ".so", ".dll" })
    if ($unexpectedNativeFiles.Count -gt 0) {
        Fail "The agent publish is not a self-contained single-file payload."
    }

    $compilerCache = Join-Path $CacheDirectory "compiler"
    New-Item -ItemType Directory -Force -Path $compilerCache | Out-Null
    Copy-Item `
        -LiteralPath $assetPaths[$reApiArchive] `
        -Destination (Join-Path $compilerCache $reApiArchive) `
        -Force
    if ($IsLinux) {
        Copy-Item `
            -LiteralPath $assetPaths[$amxxBaseArchive] `
            -Destination (Join-Path $compilerCache $amxxBaseArchive) `
            -Force
    }

    $compilerArguments = @{
        CacheDirectory = $compilerCache
    }
    if ($Offline) {
        $compilerArguments.Offline = $true
    }
    $compilerOutput = & $producerCompiler @compilerArguments
    $producerBuild = $compilerOutput |
        Where-Object { $_ -is [psobject] -and $_.PSObject.Properties.Name -contains "OutputSha256" } |
        Select-Object -Last 1
    if ($null -eq $producerBuild -or
        -not (Test-Path -LiteralPath $producerBuild.Output -PathType Leaf)) {
        Fail "The producer compiler did not return a verified plugin output."
    }

    $agentDestination = Join-Path $stageRoot "agent"
    $gameServerDestination = Join-Path $stageRoot "gameserver/cstrike"
    $amxxDestination = Join-Path $gameServerDestination "addons/amxmodx"
    New-Item -ItemType Directory -Force -Path $agentDestination | Out-Null
    New-Item -ItemType Directory -Force -Path (Split-Path $amxxDestination -Parent) | Out-Null

    Copy-RequiredFile `
        -Source $publishedAgent `
        -Destination (Join-Path $agentDestination "GoldSrcOps.GameEventAgent")
    Copy-RequiredFile `
        -Source $agentSettings `
        -Destination (Join-Path $agentDestination "appsettings.json")
    Copy-Item `
        -LiteralPath (Join-Path $amxxRoot "addons/amxmodx") `
        -Destination $amxxDestination `
        -Recurse

    $pluginsDirectory = Join-Path $amxxDestination "plugins"
    Get-ChildItem -LiteralPath $pluginsDirectory -Force |
        Remove-Item -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $amxxDestination "scripting") -Recurse -Force
    Copy-RequiredFile `
        -Source $producerBuild.Output `
        -Destination (Join-Path $pluginsDirectory "goldsrcops_game_events.amxx")

    Copy-RequiredFile `
        -Source (Join-Path $metamodRoot "addons/metamod/metamod_i386.so") `
        -Destination (Join-Path $gameServerDestination "addons/metamod/metamod_i386.so")
    Copy-RequiredFile `
        -Source (Join-Path $reApiRoot "addons/amxmodx/modules/reapi_amxx_i386.so") `
        -Destination (Join-Path $amxxDestination "modules/reapi_amxx_i386.so")

    Write-Utf8Lf `
        -Path (Join-Path $gameServerDestination "addons/metamod/plugins.ini") `
        -Content "linux addons/amxmodx/dlls/amxmodx_mm_i386.so`n"
    Write-Utf8Lf `
        -Path (Join-Path $amxxDestination "configs/plugins.ini") `
        -Content "goldsrcops_game_events.amxx`n"
    Write-Utf8Lf `
        -Path (Join-Path $amxxDestination "configs/modules.ini") `
        -Content "reapi`n"
    Write-Utf8Lf `
        -Path (Join-Path $amxxDestination "configs/amxx.cfg") `
        -Content @"
// GoldSrcOps pilot producer remains disabled until the reviewed activation step.
goldsrcops_events_enabled 0
goldsrcops_spool_incoming "addons/amxmodx/data/goldsrcops-spool/incoming"
"@
    Write-Utf8Lf `
        -Path (Join-Path $agentDestination "run.sh") `
        -Content @'
#!/usr/bin/env bash

set -Eeuo pipefail
umask 077

readonly agent_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly credential_name="oauth-client-secret"

if (($# > 1)); then
    printf 'ERROR: the game-event agent launcher accepts at most one command.\n' >&2
    exit 2
fi

agent_command="${1:-run}"
case "$agent_command" in
    run|verify-access-token) ;;
    *)
        printf 'ERROR: the game-event agent launcher command is unsupported.\n' >&2
        exit 2
        ;;
esac

if [[ -z "${CREDENTIALS_DIRECTORY:-}" ]]; then
    printf 'ERROR: systemd credentials directory is unavailable.\n' >&2
    exit 1
fi

credential_path="$CREDENTIALS_DIRECTORY/$credential_name"
if [[ ! -f "$credential_path" || -L "$credential_path" ]]; then
    printf 'ERROR: the OAuth client credential is missing or unsafe.\n' >&2
    exit 1
fi

export GameEventAgent__Delivery__OAuth__ClientSecretFile="$credential_path"
exec "$agent_directory/GoldSrcOps.GameEventAgent" "$agent_command"
'@

    $payload = @(
        Get-ChildItem -LiteralPath $stageRoot -File -Recurse |
            ForEach-Object {
                $relativePath = [IO.Path]::GetRelativePath($stageRoot, $_.FullName).Replace("\", "/")
                Assert-SafePayloadPath $relativePath
                $mode = if ($relativePath -in @(
                    "agent/GoldSrcOps.GameEventAgent",
                    "agent/run.sh")) { "0750" } else { "0640" }

                [ordered]@{
                    path = $relativePath
                    length = $_.Length
                    sha256 = Get-LowerSha256 $_.FullName
                    mode = $mode
                }
            } |
            Sort-Object { $_.path }
    )

    $requiredPayloadPaths = @(
        "agent/GoldSrcOps.GameEventAgent",
        "agent/appsettings.json",
        "agent/run.sh",
        "gameserver/cstrike/addons/metamod/metamod_i386.so",
        "gameserver/cstrike/addons/metamod/plugins.ini",
        "gameserver/cstrike/addons/amxmodx/dlls/amxmodx_mm_i386.so",
        "gameserver/cstrike/addons/amxmodx/modules/reapi_amxx_i386.so",
        "gameserver/cstrike/addons/amxmodx/plugins/goldsrcops_game_events.amxx",
        "gameserver/cstrike/addons/amxmodx/configs/plugins.ini",
        "gameserver/cstrike/addons/amxmodx/configs/modules.ini",
        "gameserver/cstrike/addons/amxmodx/configs/amxx.cfg"
    )
    $payloadPaths = @($payload | ForEach-Object { $_.path })
    foreach ($requiredPayloadPath in $requiredPayloadPaths) {
        if ($requiredPayloadPath -notin $payloadPaths) {
            Fail "The assembled bundle is missing $requiredPayloadPath."
        }
    }

    if ((Get-RepositoryRevision) -cne $SourceRevision) {
        Fail "The checked-out revision changed during bundle construction."
    }
    if ($productionEligible -and (Test-TrackedSourceDirty)) {
        Fail "Tracked source changed during production-eligible bundle construction."
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        bundleVersion = $BundleVersion
        sourceRevision = $SourceRevision
        sourceDirty = $sourceDirty
        productionEligible = $productionEligible
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        targetRuntime = "linux-x64"
        activation = [ordered]@{
            changesGameServerRuntime = $false
            producerEnabled = $false
            spoolImportEnabled = $false
            deliveryEnabled = $false
        }
        components = @(
            $assets | ForEach-Object {
                [ordered]@{
                    name = $_.Name
                    version = $_.Version
                    sourceUri = $_.Uri
                    archiveSha256 = $_.Sha256
                }
            }
        )
        producer = [ordered]@{
            sourcePath = "samples/amxmodx-game-event-producer/goldsrcops_game_events.sma"
            sourceSha256 = Get-LowerSha256 $producerSource
            compiledSha256 = $producerBuild.OutputSha256
        }
        payload = $payload
    }
    $manifestPath = Join-Path $stageRoot "manifest.json"
    Write-Utf8Lf `
        -Path $manifestPath `
        -Content (($manifest | ConvertTo-Json -Depth 8) + "`n")

    [IO.Compression.ZipFile]::CreateFromDirectory(
        $stageRoot,
        $bundlePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)
    $bundleSha256 = Get-LowerSha256 $bundlePath
    Write-Utf8Lf `
        -Path $digestPath `
        -Content "$bundleSha256  $(Split-Path $bundlePath -Leaf)`n"

    [pscustomobject]@{
        Bundle = $bundlePath
        BundleSha256 = $bundleSha256
        DigestFile = $digestPath
        BundleVersion = $BundleVersion
        SourceRevision = $SourceRevision
        ProductionEligible = $productionEligible
        PayloadFiles = $payload.Count
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
