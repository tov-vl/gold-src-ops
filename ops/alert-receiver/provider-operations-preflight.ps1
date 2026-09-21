#Requires -Version 7.0

<#
.SYNOPSIS
Validates the separately authorized provider-operations activation contract.

.DESCRIPTION
Runs the base control-plane co-location preflight, renders both reviewed
activation overlays, and proves that the Web BFF reaches the internal receiver
route with one shared file-backed authorization value. The public Caddy route
remains limited to availability-event ingestion. ContractOnly validates the
tracked placeholders without reading the target-host secret.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ReceiverEnvironmentFile,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProductionEnvironmentFile,

    [switch]$ContractOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$receiverEnvironmentPath = (Resolve-Path -LiteralPath $ReceiverEnvironmentFile).Path
$productionEnvironmentPath = (Resolve-Path -LiteralPath $ProductionEnvironmentFile).Path
$receiverComposeFile = Join-Path $PSScriptRoot "compose.yml"
$controlPlaneComposeFile = Join-Path $PSScriptRoot "compose.control-plane.yml"
$receiverOverlayFile = Join-Path $PSScriptRoot "compose.provider-operations.yml"
$productionDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../production")).Path
$productionComposeFile = Join-Path $productionDirectory "compose.yml"
$productionOverlayFile = Join-Path $productionDirectory "compose.provider-operations.yml"
$productionCaddyfile = Join-Path $productionDirectory "Caddyfile"
$pathComparison = if ($IsWindows) {
    [StringComparison]::OrdinalIgnoreCase
}
else {
    [StringComparison]::Ordinal
}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)][object]$InputObject,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Test-PathInsideRepository {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $repoRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($rootWithSeparator, $pathComparison)
}

function Invoke-ComposeConfig {
    param(
        [Parameter(Mandatory = $true)][string]$EnvironmentPath,
        [Parameter(Mandatory = $true)][string[]]$ComposeFiles
    )

    $arguments = @("--env-file", $EnvironmentPath)
    foreach ($composeFile in $ComposeFiles) {
        $arguments += @("--file", $composeFile)
    }
    $arguments += @("--profile", "runtime", "config", "--format", "json")

    $output = @(& docker compose @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Provider-operations Compose rendering failed: $($output -join [Environment]::NewLine)"
    }

    return (($output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100)
}

function Read-ValidatedAuthorization {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    Assert-Condition `
        -Condition (-not (Test-PathInsideRepository -Path $resolved)) `
        -Message "Provider operations authorization secret must live outside the repository."

    $item = Get-Item -LiteralPath $resolved
    Assert-Condition `
        -Condition ($item.Length -gt 0) `
        -Message "Provider operations authorization secret is empty."

    if (-not $IsWindows) {
        $mode = [IO.File]::GetUnixFileMode($resolved)
        $forbidden =
            [IO.UnixFileMode]::GroupRead -bor
            [IO.UnixFileMode]::GroupWrite -bor
            [IO.UnixFileMode]::GroupExecute -bor
            [IO.UnixFileMode]::OtherRead -bor
            [IO.UnixFileMode]::OtherWrite -bor
            [IO.UnixFileMode]::OtherExecute
        Assert-Condition `
            -Condition (($mode -band $forbidden) -eq 0) `
            -Message "Provider operations authorization secret must not be accessible by group or other users."
        $ownerId = ((& stat --format=%u -- $resolved) -join "").Trim()
        Assert-Condition `
            -Condition ($LASTEXITCODE -eq 0 -and $ownerId -eq "1654") `
            -Message "Provider operations authorization secret must be owned by Unix UID 1654."
    }

    $value = [IO.File]::ReadAllText($resolved)
    Assert-Condition `
        -Condition ($value -notmatch '[\r\n]' -and
            $value.Length -le 8192 -and
            $value -match '\ABearer [^\s]+\z') `
        -Message "Provider operations authorization must be a bounded single-line Bearer credential."
}

& (Join-Path $PSScriptRoot "control-plane-preflight.ps1") `
    -ReceiverEnvironmentFile $receiverEnvironmentPath `
    -ProductionEnvironmentFile $productionEnvironmentPath `
    -ContractOnly:$ContractOnly

$receiverConfiguration = Invoke-ComposeConfig `
    -EnvironmentPath $receiverEnvironmentPath `
    -ComposeFiles @($receiverComposeFile, $controlPlaneComposeFile, $receiverOverlayFile)
$productionConfiguration = Invoke-ComposeConfig `
    -EnvironmentPath $productionEnvironmentPath `
    -ComposeFiles @($productionComposeFile, $productionOverlayFile)

$receiver = $receiverConfiguration.services.receiver
$web = $productionConfiguration.services.web
$receiverSecretSources = @($receiver.secrets | ForEach-Object { $_.source } | Sort-Object)
$webSecretSources = @($web.secrets | ForEach-Object { $_.source } | Sort-Object)

Assert-Condition `
    -Condition (@(Compare-Object @(
                "provider-operations-authorization",
                "receiver-authorization",
                "receiver-database-connection") $receiverSecretSources).Count -eq 0) `
    -Message "Provider-operations receiver must receive only database, ingress, and operations authorization secrets."
Assert-Condition `
    -Condition (@(Compare-Object @(
                "provider-operations-authorization",
                "web-data-protection-certificate",
                "web-data-protection-certificate-password",
                "web-oidc-client-secret") $webSecretSources).Count -eq 0) `
    -Message "Provider-operations Web host must receive only its existing secrets and operations authorization."
Assert-Condition `
    -Condition ([string]$receiver.environment.ProviderOperations__Enabled -eq "true" -and
        [string]$receiver.environment.ProviderDelivery__Enabled -eq "false" -and
        [string]$receiver.environment.Receiver__Mode -eq "CatchUp" -and
        $null -eq (Get-PropertyValue -InputObject $receiver.environment -Name "ProviderOperations__Authorization")) `
    -Message "Provider operations must activate independently without enabling delivery or exposing authorization in the environment."
Assert-Condition `
    -Condition ([string]$web.environment.ProviderOperations__Enabled -eq "true" -and
        [string]$web.environment.ProviderOperations__BaseUrl -eq "http://goldsrcops-alert-receiver:8080/" -and
        [string]$web.environment.ProviderOperations__AuthorizationFile -eq
            "/run/secrets/provider-operations-authorization" -and
        $null -eq (Get-PropertyValue -InputObject $web.environment -Name "ProviderOperations__Authorization")) `
    -Message "Web provider operations must use the private receiver alias and file-backed authorization."

$receiverSecretPath = [IO.Path]::GetFullPath(
    [string]$receiverConfiguration.secrets.'provider-operations-authorization'.file)
$webSecretPath = [IO.Path]::GetFullPath(
    [string]$productionConfiguration.secrets.'provider-operations-authorization'.file)
Assert-Condition `
    -Condition ($receiverSecretPath.Equals($webSecretPath, $pathComparison)) `
    -Message "Receiver and Web must reference the same owner-controlled provider operations authorization file."

$caddyText = [IO.File]::ReadAllText($productionCaddyfile)
Assert-Condition `
    -Condition (-not $caddyText.Contains(
        "/internal/v1/provider-delivery",
        [StringComparison]::Ordinal)) `
    -Message "Production Caddy must not expose provider operations routes."

if (-not $ContractOnly) {
    Assert-Condition `
        -Condition (-not (Test-PathInsideRepository -Path $receiverEnvironmentPath) -and
            -not (Test-PathInsideRepository -Path $productionEnvironmentPath)) `
        -Message "Provider-operations deployment environments must live outside the repository."
    Read-ValidatedAuthorization -Path $receiverSecretPath
}

$mode = if ($ContractOnly) { "contract" } else { "deployment" }
Write-Host "Provider operations cross-stack $mode preflight passed."
