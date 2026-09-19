#Requires -Version 7.0

<#
.SYNOPSIS
Validates the fail-closed AlertReceiver deployment contract.

.DESCRIPTION
Renders the tracked Compose file and verifies immutable images, isolated
PostgreSQL access, one-shot migrations, file-backed secrets, hardened runtime
containers, bounded logs, and the CatchUp/provider-disabled activation state.
ContractOnly validates the tracked example without reading target-host secrets.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$EnvironmentFile,

    [switch]$ContractOnly,

    [switch]$ControlPlane
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$composeFile = Join-Path $PSScriptRoot "compose.yml"
$controlPlaneComposeFile = Join-Path $PSScriptRoot "compose.control-plane.yml"
$environmentPath = (Resolve-Path -LiteralPath $EnvironmentFile).Path

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Assert-ImmutableImage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Image,

        [Parameter(Mandatory = $true)]
        [string]$ServiceName
    )

    Assert-Condition `
        -Condition ($Image -match '\A[^@\s]+@sha256:(?<digest>[0-9a-f]{64})\z') `
        -Message "Service '$ServiceName' must use an immutable sha256 image reference."

    if (-not $ContractOnly) {
        Assert-Condition `
            -Condition ($Matches["digest"] -notmatch '\A([0-9a-f])\1{63}\z') `
            -Message "Service '$ServiceName' still uses a placeholder image digest."
    }
}

function Assert-BoundedLogging {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Service,

        [Parameter(Mandatory = $true)]
        [string]$ServiceName
    )

    Assert-Condition `
        -Condition ([string]$Service.logging.driver -eq "local" -and
            [string]$Service.logging.options."max-file" -eq "5" -and
            [string]$Service.logging.options."max-size" -eq "10m") `
        -Message "Service '$ServiceName' must use bounded local logging."
}

function Assert-HardenedService {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Service,

        [Parameter(Mandatory = $true)]
        [string]$ServiceName
    )

    Assert-Condition `
        -Condition ([bool]$Service.read_only -and
            @($Service.cap_drop) -contains "ALL" -and
            @($Service.security_opt) -contains "no-new-privileges:true") `
        -Message "Service '$ServiceName' must use the hardened read-only container contract."
    Assert-Condition `
        -Condition ($null -eq (Get-PropertyValue -InputObject $Service -Name "ports")) `
        -Message "Service '$ServiceName' must not publish host ports."
}

function Test-PathInsideRepository {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $repoRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    return $fullPath.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SecretFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [int]$RequiredOwnerId
    )

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    Assert-Condition `
        -Condition ((Get-Item -LiteralPath $resolved).Length -gt 0) `
        -Message "$Name secret is empty."
    Assert-Condition `
        -Condition (-not (Test-PathInsideRepository -Path $resolved)) `
        -Message "$Name secret must live outside the repository."

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
            -Condition ($LASTEXITCODE -eq 0 -and $ownerId -eq [string]$RequiredOwnerId) `
            -Message "$Name secret must be owned by Unix UID $RequiredOwnerId."
    }
}

$composeArguments = @(
    "--env-file", $environmentPath,
    "--file", $composeFile
)
if ($ControlPlane) {
    $composeArguments += @("--file", $controlPlaneComposeFile)
}
else {
    $composeArguments += @("--profile", "standalone")
}
$composeArguments += @(
    "--profile", "runtime",
    "--profile", "operations",
    "config", "--format", "json"
)

$composeOutput = @(& docker compose @composeArguments 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "AlertReceiver Compose rendering failed: $($composeOutput -join [Environment]::NewLine)"
}

$configuration = ($composeOutput -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100
$postgres = $configuration.services.postgres
$receiver = $configuration.services.receiver
$migration = $configuration.services.migration
$caddy = Get-PropertyValue -InputObject $configuration.services -Name "caddy"

Assert-ImmutableImage -Image $postgres.image -ServiceName "postgres"
Assert-ImmutableImage -Image $receiver.image -ServiceName "receiver"
Assert-ImmutableImage -Image $migration.image -ServiceName "migration"
if (-not $ControlPlane) {
    Assert-ImmutableImage -Image $caddy.image -ServiceName "caddy"
}
Assert-Condition `
    -Condition ($receiver.image -eq $migration.image) `
    -Message "Receiver runtime and migration must use the same image digest."

foreach ($service in @(
        @{ Name = "postgres"; Value = $postgres },
        @{ Name = "receiver"; Value = $receiver },
        @{ Name = "migration"; Value = $migration }
    )) {
    Assert-BoundedLogging -Service $service.Value -ServiceName $service.Name
}
if (-not $ControlPlane) {
    Assert-BoundedLogging -Service $caddy -ServiceName "caddy"
}

Assert-HardenedService -Service $receiver -ServiceName "receiver"
Assert-HardenedService -Service $migration -ServiceName "migration"
Assert-Condition `
    -Condition ([string]$postgres.network_mode -eq "none") `
    -Message "Receiver PostgreSQL must have no network interface."
Assert-Condition `
    -Condition ([string]$migration.network_mode -eq "none") `
    -Message "Receiver migration must have no network interface."
Assert-Condition `
    -Condition ([string]$postgres.environment.POSTGRES_DB -eq "goldsrcops_receiver" -and
        [string]$postgres.environment.POSTGRES_USER -eq "goldsrcops_receiver") `
    -Message "Receiver PostgreSQL must use its dedicated database and role."
Assert-Condition `
    -Condition ([double]$postgres.deploy.resources.limits.cpus -eq 1.0 -and
        [int64]$postgres.deploy.resources.limits.memory -eq 1GB) `
    -Message "Receiver PostgreSQL must keep its reviewed 1 CPU / 1 GiB co-location limit."
Assert-Condition `
    -Condition ([double]$receiver.deploy.resources.limits.cpus -eq 0.5 -and
        [int64]$receiver.deploy.resources.limits.memory -eq 512MB) `
    -Message "Receiver runtime must keep its reviewed 0.5 CPU / 512 MiB co-location limit."

$receiverSecrets = @($receiver.secrets | ForEach-Object { $_.source } | Sort-Object)
Assert-Condition `
    -Condition (@(Compare-Object @(
                "receiver-authorization",
                "receiver-database-connection") $receiverSecrets).Count -eq 0) `
    -Message "Receiver runtime must receive only its database and ingress authorization secrets."
$migrationSecrets = @($migration.secrets | ForEach-Object { $_.source })
Assert-Condition `
    -Condition ($migrationSecrets.Count -eq 1 -and
        $migrationSecrets[0] -eq "receiver-database-connection") `
    -Message "Receiver migration must receive only its database connection secret."
Assert-Condition `
    -Condition ($null -eq (Get-PropertyValue -InputObject $receiver.environment -Name "Receiver__Authorization") -and
        $null -eq (Get-PropertyValue -InputObject $receiver.environment -Name "ConnectionStrings__AlertReceiver") -and
        $null -eq (Get-PropertyValue -InputObject $receiver.environment -Name "ProviderDelivery__Authorization")) `
    -Message "Receiver secrets must not appear in Compose environment values."
Assert-Condition `
    -Condition ([string]$receiver.environment.Receiver__Mode -eq "CatchUp" -and
        [string]$receiver.environment.ProviderDelivery__Enabled -eq "false") `
    -Message "The base deployment must start in CatchUp mode with provider delivery disabled."

$migrationCommand = @($migration.command)
Assert-Condition `
    -Condition ($migrationCommand.Count -eq 3 -and
        $migrationCommand[0] -eq "migrate" -and
        $migrationCommand[1] -eq "--no-color" -and
        $migrationCommand[2] -eq "--prefix-output") `
    -Message "Receiver migration must invoke the image-contained bundle."
Assert-Condition `
    -Condition (@($migration.profiles) -contains "operations" -and
        @($migration.profiles) -notcontains "runtime" -and
        $null -eq (Get-PropertyValue -InputObject $migration -Name "restart")) `
    -Message "Receiver migration must remain a one-shot operations action."
Assert-Condition `
    -Condition ([string]$migration.environment.DOTNET_BUNDLE_EXTRACT_BASE_DIR -eq "/tmp/.net") `
    -Message "Receiver migration bundle must extract only into bounded tmpfs."
Assert-Condition `
    -Condition (@($migration.tmpfs).Count -eq 1 -and
        [string]$migration.tmpfs[0] -eq "/tmp:rw,noexec,nosuid,size=64m,mode=0700,uid=1654,gid=1654") `
    -Message "Receiver migration tmpfs must match the rehearsed size and non-root ownership."

$socketSources = @(
    ($postgres.volumes | Where-Object { $_.target -eq "/var/run/postgresql" }).source,
    ($receiver.volumes | Where-Object { $_.target -eq "/var/run/postgresql" }).source,
    ($migration.volumes | Where-Object { $_.target -eq "/var/run/postgresql" }).source
)
Assert-Condition `
    -Condition ($socketSources.Count -eq 3 -and
        @($socketSources | Sort-Object -Unique).Count -eq 1) `
    -Message "PostgreSQL, receiver, and migration must share exactly one Unix socket volume."

if ($ControlPlane) {
    Assert-Condition `
        -Condition ($null -eq $caddy) `
        -Message "Control-plane co-location must not start a second Caddy service."
    Assert-Condition `
        -Condition ([bool]$configuration.networks.edge.external -and
            -not [string]::IsNullOrWhiteSpace([string]$configuration.networks.edge.name)) `
        -Message "Control-plane co-location must join one explicitly named external edge network."
}
else {
    $expectedPorts = @("80/tcp", "443/tcp", "443/udp")
    $actualPorts = @($caddy.ports | ForEach-Object { "$($_.published)/$($_.protocol)" } | Sort-Object)
    Assert-Condition `
        -Condition (@(Compare-Object ($expectedPorts | Sort-Object) $actualPorts).Count -eq 0) `
        -Message "Caddy must be the only service publishing TCP 80 and TCP/UDP 443."
    Assert-Condition `
        -Condition (@($caddy.depends_on.PSObject.Properties.Name).Count -eq 1 -and
            @($caddy.depends_on.PSObject.Properties.Name)[0] -eq "receiver") `
        -Message "Receiver Caddy must depend only on the receiver runtime."

    $caddyMount = @($caddy.volumes | Where-Object { $_.target -eq "/etc/caddy/Caddyfile" })
    Assert-Condition `
        -Condition ($caddyMount.Count -eq 1 -and [bool]$caddyMount[0].read_only -and
            [IO.Path]::GetFullPath([string]$caddyMount[0].source) -eq
                [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "Caddyfile"))) `
        -Message "Receiver Caddy must use the tracked read-only Caddyfile."
}

if (-not $ContractOnly) {
    Assert-Condition `
        -Condition (-not (Test-PathInsideRepository -Path $environmentPath)) `
        -Message "Receiver deployment environment must live outside the repository."

    $hostname = [string]$receiver.environment.AllowedHosts.Split(';')[0]
    Assert-Condition `
        -Condition ($hostname -notin @("localhost", "127.0.0.1") -and
            $hostname -notmatch '(?i)(^|\.)example\.(com|net|org)$') `
        -Message "Receiver hostname still uses a local or placeholder value."

    Assert-SecretFile `
        -Path ([string]$configuration.secrets.'receiver-postgres-password'.file) `
        -Name "Receiver PostgreSQL password" `
        -RequiredOwnerId 0
    Assert-SecretFile `
        -Path ([string]$configuration.secrets.'receiver-database-connection'.file) `
        -Name "Receiver database connection" `
        -RequiredOwnerId 1654
    Assert-SecretFile `
        -Path ([string]$configuration.secrets.'receiver-authorization'.file) `
        -Name "Receiver ingress authorization" `
        -RequiredOwnerId 1654

    $images = @($postgres.image, $receiver.image)
    if (-not $ControlPlane) {
        $images += $caddy.image
    }
    foreach ($image in $images) {
        & docker pull $image
        if ($LASTEXITCODE -ne 0) {
            throw "A digest-pinned AlertReceiver deployment image could not be pulled."
        }
    }

    $runtimeUser = ((& docker image inspect --format "{{.Config.User}}" $receiver.image) -join "").Trim()
    Assert-Condition `
        -Condition ($LASTEXITCODE -eq 0 -and $runtimeUser -eq "1654") `
        -Message "AlertReceiver image must run as Unix UID 1654."
    & docker run --rm --entrypoint /bin/sh $receiver.image -ec `
        "test -x /app/goldsrcops-alert-receiver-migrate && test -x /app/receiver-entrypoint.sh && test ! -e /app/appsettings.Development.json"
    if ($LASTEXITCODE -ne 0) {
        throw "AlertReceiver image does not contain its reviewed migration and entrypoint contract."
    }

    if (-not $ControlPlane) {
        & docker run `
            --rm `
            --env "GOLDSRCOPS_ALERT_RECEIVER_ACME_EMAIL=$($caddy.environment.GOLDSRCOPS_ALERT_RECEIVER_ACME_EMAIL)" `
            --env "GOLDSRCOPS_ALERT_RECEIVER_HOSTNAME=$hostname" `
            --volume "${PSScriptRoot}/Caddyfile:/etc/caddy/Caddyfile:ro" `
            $caddy.image `
            caddy validate --config /etc/caddy/Caddyfile
        if ($LASTEXITCODE -ne 0) {
            throw "AlertReceiver Caddy configuration validation failed."
        }
    }
}

$mode = if ($ContractOnly) { "contract" } else { "deployment" }
$placement = if ($ControlPlane) { "control-plane co-location" } else { "standalone" }
Write-Host "AlertReceiver $placement Compose $mode preflight passed."
