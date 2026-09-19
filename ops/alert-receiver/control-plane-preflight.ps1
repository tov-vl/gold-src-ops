#Requires -Version 7.0

<#
.SYNOPSIS
Validates AlertReceiver co-location behind the existing control-plane Caddy.

.DESCRIPTION
Runs both deployment preflights, then proves that the receiver joins the
existing production edge network, publishes no host ports, and is reachable
only through the bounded production Caddy route. ContractOnly uses the tracked
placeholder environments and reads no target-host secret.
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

$receiverEnvironmentPath = (Resolve-Path -LiteralPath $ReceiverEnvironmentFile).Path
$productionEnvironmentPath = (Resolve-Path -LiteralPath $ProductionEnvironmentFile).Path
$receiverComposeFile = Join-Path $PSScriptRoot "compose.yml"
$controlPlaneComposeFile = Join-Path $PSScriptRoot "compose.control-plane.yml"
$productionDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../production")).Path
$productionComposeFile = Join-Path $productionDirectory "compose.yml"
$productionCaddyfile = Join-Path $productionDirectory "Caddyfile"

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
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
    $arguments += @(
        "--profile", "runtime",
        "--profile", "operations",
        "config", "--format", "json"
    )

    $output = @(& docker compose @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Compose rendering failed: $($output -join [Environment]::NewLine)"
    }

    return (($output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100)
}

& (Join-Path $PSScriptRoot "preflight.ps1") `
    -EnvironmentFile $receiverEnvironmentPath `
    -ContractOnly:$ContractOnly `
    -ControlPlane
& (Join-Path $productionDirectory "preflight.ps1") `
    -EnvironmentFile $productionEnvironmentPath `
    -ContractOnly:$ContractOnly

$receiverConfiguration = Invoke-ComposeConfig `
    -EnvironmentPath $receiverEnvironmentPath `
    -ComposeFiles @($receiverComposeFile, $controlPlaneComposeFile)
$productionConfiguration = Invoke-ComposeConfig `
    -EnvironmentPath $productionEnvironmentPath `
    -ComposeFiles @($productionComposeFile)

$receiver = $receiverConfiguration.services.receiver
$productionCaddy = $productionConfiguration.services.caddy
$receiverHost = [string]$receiver.environment.AllowedHosts.Split(';')[0]
$receiverEdgeName = [string]$receiverConfiguration.networks.edge.name
$productionEdgeName = [string]$productionConfiguration.networks.edge.name

Assert-Condition `
    -Condition ($null -eq $receiverConfiguration.services.PSObject.Properties["caddy"]) `
    -Message "Co-location must not activate the standalone AlertReceiver Caddy service."
Assert-Condition `
    -Condition ($null -eq $receiver.PSObject.Properties["ports"]) `
    -Message "The co-located AlertReceiver must not publish host ports."
Assert-Condition `
    -Condition ([bool]$receiverConfiguration.networks.edge.external -and
        -not [string]::IsNullOrWhiteSpace($receiverEdgeName) -and
        $receiverEdgeName -eq $productionEdgeName) `
    -Message "AlertReceiver and production Caddy must share the same named external edge network."
Assert-Condition `
    -Condition (@($receiver.networks.edge.aliases) -contains "goldsrcops-alert-receiver") `
    -Message "The co-located receiver must expose only its reviewed private network alias."
Assert-Condition `
    -Condition ([string]$productionCaddy.environment.GOLDSRCOPS_ALERT_RECEIVER_HOSTNAME -eq $receiverHost) `
    -Message "AlertReceiver and production Caddy must use the same receiver hostname."

$caddyText = [IO.File]::ReadAllText($productionCaddyfile)
foreach ($requiredFragment in @(
        '{$GOLDSRCOPS_ALERT_RECEIVER_HOSTNAME}',
        'method POST',
        'path /api/v1/availability-events',
        'reverse_proxy goldsrcops-alert-receiver:8080',
        'respond 404'
    )) {
    Assert-Condition `
        -Condition ($caddyText.Contains($requiredFragment, [StringComparison]::Ordinal)) `
        -Message "Production Caddyfile is missing the bounded AlertReceiver fragment: $requiredFragment"
}

$mode = if ($ContractOnly) { "contract" } else { "deployment" }
Write-Host "AlertReceiver control-plane co-location $mode preflight passed."
