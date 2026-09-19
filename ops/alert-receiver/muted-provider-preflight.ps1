#Requires -Version 7.0

<#
.SYNOPSIS
Validates the bounded muted-provider AlertReceiver overlay.

.DESCRIPTION
Runs the base deployment preflight, renders the base and muted-provider Compose
files together, and verifies that provider delivery is enabled only in Live
mode with one dispatcher and two file-backed provider secrets. Deployment mode
also validates the secret files without printing their contents.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$EnvironmentFile,

    [switch]$ContractOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$baseComposeFile = Join-Path $PSScriptRoot "compose.yml"
$overlayComposeFile = Join-Path $PSScriptRoot "compose.muted-provider.yml"
$environmentPath = (Resolve-Path -LiteralPath $EnvironmentFile).Path

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-PathInsideRepository {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $repoRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)
}

function Read-ValidatedSecret {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    Assert-Condition `
        -Condition (-not (Test-PathInsideRepository -Path $resolved)) `
        -Message "$Name secret must live outside the repository."

    $item = Get-Item -LiteralPath $resolved
    Assert-Condition -Condition ($item.Length -gt 0) -Message "$Name secret is empty."

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
            -Message "$Name secret must not be accessible by group or other users."
        $ownerId = ((& stat --format=%u -- $resolved) -join "").Trim()
        Assert-Condition `
            -Condition ($LASTEXITCODE -eq 0 -and $ownerId -eq "1654") `
            -Message "$Name secret must be owned by Unix UID 1654."
    }

    $value = [IO.File]::ReadAllText($resolved)
    Assert-Condition `
        -Condition ($value -notmatch '[\r\n]') `
        -Message "$Name secret must contain exactly one line."
    return $value
}

& (Join-Path $PSScriptRoot "preflight.ps1") `
    -EnvironmentFile $environmentPath `
    -ContractOnly:$ContractOnly

$composeOutput = @(& docker compose `
        --env-file $environmentPath `
        --file $baseComposeFile `
        --file $overlayComposeFile `
        --profile runtime `
        config `
        --format json 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Muted-provider Compose rendering failed: $($composeOutput -join [Environment]::NewLine)"
}

$configuration = ($composeOutput -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100
$receiver = $configuration.services.receiver
$receiverSecrets = @($receiver.secrets | ForEach-Object { $_.source } | Sort-Object)
$expectedSecrets = @(
    "provider-authorization",
    "provider-endpoint",
    "receiver-authorization",
    "receiver-database-connection"
)

Assert-Condition `
    -Condition (@(Compare-Object $expectedSecrets $receiverSecrets).Count -eq 0) `
    -Message "Muted-provider receiver must receive exactly its database, ingress, endpoint, and provider authorization secrets."
Assert-Condition `
    -Condition ([string]$receiver.environment.Receiver__Mode -eq "Live" -and
        [string]$receiver.environment.ProviderDelivery__Enabled -eq "true" -and
        [string]$receiver.environment.ProviderDelivery__MaxConcurrency -eq "1") `
    -Message "Muted-provider trial must use Live mode with exactly one provider dispatcher."
Assert-Condition `
    -Condition ($null -eq $receiver.environment.PSObject.Properties["ProviderDelivery__Endpoint"] -and
        $null -eq $receiver.environment.PSObject.Properties["ProviderDelivery__Authorization"]) `
    -Message "Provider endpoint and authorization must not appear in Compose environment values."
Assert-Condition `
    -Condition ([bool]$receiver.read_only -and
        @($receiver.cap_drop) -contains "ALL" -and
        @($receiver.security_opt) -contains "no-new-privileges:true") `
    -Message "Muted-provider overlay must preserve the hardened receiver runtime."

if (-not $ContractOnly) {
    Assert-Condition `
        -Condition (-not (Test-PathInsideRepository -Path $environmentPath)) `
        -Message "Muted-provider deployment environment must live outside the repository."

    $endpoint = Read-ValidatedSecret `
        -Path ([string]$configuration.secrets.'provider-endpoint'.file) `
        -Name "Provider endpoint"
    $authorization = Read-ValidatedSecret `
        -Path ([string]$configuration.secrets.'provider-authorization'.file) `
        -Name "Provider authorization"

    $endpointUri = $null
    Assert-Condition `
        -Condition ([Uri]::TryCreate($endpoint, [UriKind]::Absolute, [ref]$endpointUri) -and
            $endpointUri.Scheme -eq [Uri]::UriSchemeHttps -and
            [string]::IsNullOrEmpty($endpointUri.UserInfo) -and
            [string]::IsNullOrEmpty($endpointUri.Fragment)) `
        -Message "Provider endpoint must be an absolute HTTPS URI without user info or fragment."
    Assert-Condition `
        -Condition ($authorization.Length -le 8192 -and $authorization -match '\ABearer [^\s]+\z') `
        -Message "Provider authorization must be a bounded single-line Bearer credential."
}

$mode = if ($ContractOnly) { "contract" } else { "deployment" }
Write-Host "AlertReceiver muted-provider $mode preflight passed."
