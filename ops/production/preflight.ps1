#Requires -Version 7.0

<#
.SYNOPSIS
Validates the provider-independent GoldSrcOps production Compose contract.

.DESCRIPTION
Renders the tracked Compose file with a deployment environment file and checks
the immutable image, one-shot migration, network, proxy, secret, and port
boundaries without printing secret values. ContractOnly validates the tracked
example in CI while skipping target-host files and non-placeholder deployment
values.
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
$composeFile = Join-Path $PSScriptRoot "compose.yml"
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

function Assert-IPv4Address {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Address,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $parsed = $null
    Assert-Condition `
        -Condition ([System.Net.IPAddress]::TryParse($Address, [ref]$parsed) -and
            $parsed.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) `
        -Message "$Name must be a valid IPv4 address."
}

function Test-AddressInCidr {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Address,

        [Parameter(Mandatory = $true)]
        [string]$Cidr
    )

    $parts = $Cidr.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -ne 2) {
        return $false
    }

    $networkAddress = $null
    $addressValue = $null
    $prefixLength = 0
    if (-not [System.Net.IPAddress]::TryParse($parts[0], [ref]$networkAddress) -or
        $networkAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
        -not [int]::TryParse($parts[1], [ref]$prefixLength) -or
        $prefixLength -lt 1 -or
        $prefixLength -gt 30 -or
        -not [System.Net.IPAddress]::TryParse($Address, [ref]$addressValue) -or
        $addressValue.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        return $false
    }

    $networkBytes = $networkAddress.GetAddressBytes()
    $addressBytes = $addressValue.GetAddressBytes()
    $wholeBytes = [Math]::Floor($prefixLength / 8)
    $remainingBits = $prefixLength % 8

    for ($index = 0; $index -lt $wholeBytes; $index++) {
        if ($networkBytes[$index] -ne $addressBytes[$index]) {
            return $false
        }
    }

    if ($remainingBits -eq 0) {
        return $true
    }

    $mask = 256 - [Math]::Pow(2, 8 - $remainingBits)
    return (($networkBytes[$wholeBytes] -band $mask) -eq
        ($addressBytes[$wholeBytes] -band $mask))
}

function Get-VolumeSource {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Service,

        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    $mount = @($Service.volumes | Where-Object { $_.target -eq $Target })
    Assert-Condition `
        -Condition ($mount.Count -eq 1) `
        -Message "Expected one '$Target' mount."

    return [string]$mount[0].source
}

function Test-PathInsideRepository {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $repoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    return $fullPath.StartsWith(
        $rootWithSeparator,
        [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SecretFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [int]$RequiredOwnerId = -1
    )

    Assert-Condition `
        -Condition (-not (Test-PathInsideRepository -Path $Path)) `
        -Message "$Name secret must live outside the repository."
    Assert-Condition `
        -Condition (Test-Path -LiteralPath $Path -PathType Leaf) `
        -Message "$Name secret file does not exist."
    Assert-Condition `
        -Condition ((Get-Item -LiteralPath $Path).Length -gt 0) `
        -Message "$Name secret file is empty."

    if (-not $IsWindows) {
        $mode = [System.IO.File]::GetUnixFileMode($Path)
        $forbiddenMode =
            [System.IO.UnixFileMode]::GroupRead -bor
            [System.IO.UnixFileMode]::GroupWrite -bor
            [System.IO.UnixFileMode]::GroupExecute -bor
            [System.IO.UnixFileMode]::OtherRead -bor
            [System.IO.UnixFileMode]::OtherWrite -bor
            [System.IO.UnixFileMode]::OtherExecute

        Assert-Condition `
            -Condition (($mode -band $forbiddenMode) -eq 0) `
            -Message "$Name secret file must not be accessible by group or other users."

        if ($RequiredOwnerId -ge 0) {
            $ownerId = ((& stat --format=%u -- $Path) -join "").Trim()
            if ($LASTEXITCODE -ne 0) {
                throw "Could not determine the owner of the $Name secret file."
            }

            Assert-Condition `
                -Condition ($ownerId -eq $RequiredOwnerId.ToString([Globalization.CultureInfo]::InvariantCulture)) `
                -Message "$Name secret file must be owned by Unix UID $RequiredOwnerId."
        }
    }
}

$composeOutput = @(& docker compose `
    --env-file $environmentPath `
    --file $composeFile `
    --profile runtime `
    --profile operations `
    config `
    --format json)

if ($LASTEXITCODE -ne 0) {
    throw "docker compose config failed with exit code $LASTEXITCODE."
}

$configuration = ($composeOutput -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100
$postgres = $configuration.services.postgres
$api = $configuration.services.api
$migration = $configuration.services.migration
$caddy = $configuration.services.caddy

Assert-ImmutableImage -Image $postgres.image -ServiceName "postgres"
Assert-ImmutableImage -Image $api.image -ServiceName "api"
Assert-ImmutableImage -Image $migration.image -ServiceName "migration"
Assert-ImmutableImage -Image $caddy.image -ServiceName "caddy"
Assert-Condition `
    -Condition ($migration.image -eq $api.image) `
    -Message "The migration action must use the exact API image digest."

Assert-Condition `
    -Condition ($postgres.network_mode -eq "none") `
    -Message "PostgreSQL must run without a network interface."
Assert-Condition `
    -Condition ($null -eq (Get-PropertyValue -InputObject $postgres -Name "ports")) `
    -Message "PostgreSQL must not publish ports."
Assert-Condition `
    -Condition ($null -eq (Get-PropertyValue -InputObject $api -Name "ports")) `
    -Message "The API must not publish ports."
Assert-Condition `
    -Condition ([bool]$api.read_only) `
    -Message "The API root filesystem must be read-only."
Assert-Condition `
    -Condition (@($api.cap_drop) -contains "ALL") `
    -Message "The API must drop all Linux capabilities."
Assert-Condition `
    -Condition (@($api.security_opt) -contains "no-new-privileges:true") `
    -Message "The API must enable no-new-privileges."
Assert-Condition `
    -Condition ($migration.network_mode -eq "none") `
    -Message "The migration action must run without a network interface."
Assert-Condition `
    -Condition ($null -eq (Get-PropertyValue -InputObject $migration -Name "ports")) `
    -Message "The migration action must not publish ports."
Assert-Condition `
    -Condition ([bool]$migration.read_only) `
    -Message "The migration action root filesystem must be read-only."
Assert-Condition `
    -Condition (@($migration.cap_drop) -contains "ALL") `
    -Message "The migration action must drop all Linux capabilities."
Assert-Condition `
    -Condition (@($migration.security_opt) -contains "no-new-privileges:true") `
    -Message "The migration action must enable no-new-privileges."
Assert-Condition `
    -Condition ($null -eq (Get-PropertyValue -InputObject $migration -Name "restart")) `
    -Message "The one-shot migration action must not have a restart policy."

$migrationCommand = @($migration.command)
Assert-Condition `
    -Condition ($migrationCommand.Count -eq 3 -and
        $migrationCommand[0] -eq "migrate" -and
        $migrationCommand[1] -eq "--no-color" -and
        $migrationCommand[2] -eq "--prefix-output") `
    -Message "The migration action must invoke the tracked migrate entrypoint mode."
Assert-Condition `
    -Condition (@($migration.profiles) -contains "operations" -and
        @($migration.profiles) -notcontains "runtime") `
    -Message "The migration action must be isolated in the operations profile."
$apiEntrypoint = @($api.entrypoint)
$migrationEntrypoint = @($migration.entrypoint)
Assert-Condition `
    -Condition ($apiEntrypoint.Count -eq 2 -and
        $apiEntrypoint[0] -eq "/bin/sh" -and
        $apiEntrypoint[1] -eq "/app/api-entrypoint.sh") `
    -Message "The secret-loading entrypoint must come from the immutable API image."
Assert-Condition `
    -Condition ($migrationEntrypoint.Count -eq $apiEntrypoint.Count -and
        $migrationEntrypoint[0] -eq $apiEntrypoint[0] -and
        $migrationEntrypoint[1] -eq $apiEntrypoint[1]) `
    -Message "The API and migration action must use the same secret-loading entrypoint."
Assert-Condition `
    -Condition ([string]$migration.environment.DOTNET_BUNDLE_EXTRACT_BASE_DIR -eq "/tmp/.net") `
    -Message "The migration bundle must extract only into the bounded writable tmpfs."

$migrationSecretSources = @($migration.secrets | ForEach-Object { $_.source })
Assert-Condition `
    -Condition ($migrationSecretSources.Count -eq 1 -and
        $migrationSecretSources[0] -eq "database-connection") `
    -Message "The migration action must receive only the database connection secret."

$postgresSocketSource = Get-VolumeSource -Service $postgres -Target "/var/run/postgresql"
$apiSocketSource = Get-VolumeSource -Service $api -Target "/var/run/postgresql"
$migrationSocketSource = Get-VolumeSource -Service $migration -Target "/var/run/postgresql"
Assert-Condition `
    -Condition ($postgresSocketSource -eq $apiSocketSource) `
    -Message "PostgreSQL and the API must share the same Unix socket volume."
Assert-Condition `
    -Condition ($postgresSocketSource -eq $migrationSocketSource) `
    -Message "PostgreSQL and the migration action must share the same Unix socket volume."

$proxyAddress = [string]$api.environment.ReverseProxy__KnownProxy
$apiAddress = [string]$api.networks.edge.ipv4_address
$caddyAddress = [string]$caddy.networks.edge.ipv4_address
$edgeSubnet = [string]$configuration.networks.edge.ipam.config[0].subnet

Assert-IPv4Address -Address $proxyAddress -Name "ReverseProxy__KnownProxy"
Assert-IPv4Address -Address $apiAddress -Name "API edge address"
Assert-IPv4Address -Address $caddyAddress -Name "Caddy edge address"
Assert-Condition `
    -Condition ($proxyAddress -eq $caddyAddress) `
    -Message "The API must trust only the configured Caddy address."
Assert-Condition `
    -Condition ($apiAddress -ne $caddyAddress) `
    -Message "The API and Caddy must use different edge addresses."
Assert-Condition `
    -Condition (Test-AddressInCidr -Address $apiAddress -Cidr $edgeSubnet) `
    -Message "The API address must belong to the edge subnet."
Assert-Condition `
    -Condition (Test-AddressInCidr -Address $caddyAddress -Cidr $edgeSubnet) `
    -Message "The Caddy address must belong to the edge subnet."

Assert-Condition `
    -Condition ($null -eq (Get-PropertyValue -InputObject $api.environment -Name "ASPNETCORE_FORWARDEDHEADERS_ENABLED")) `
    -Message "Do not enable unrestricted ASP.NET Core forwarded headers."
Assert-Condition `
    -Condition ($null -eq (Get-PropertyValue -InputObject $api.environment -Name "ConnectionStrings__GoldSrcOps")) `
    -Message "The database connection string must not be stored in Compose environment values."
Assert-Condition `
    -Condition (@($api.environment.PSObject.Properties.Name | Where-Object { $_ -like "RconSecrets__*" }).Count -eq 0) `
    -Message "RCON passwords must not be stored in Compose environment values."
Assert-Condition `
    -Condition ($null -eq (Get-PropertyValue -InputObject $migration.environment -Name "ConnectionStrings__GoldSrcOps")) `
    -Message "The migration connection string must not be stored in Compose environment values."
Assert-Condition `
    -Condition ($null -eq (Get-PropertyValue -InputObject $migration.environment -Name "GOLDSRCOPS_RCON_SECRET_ALIAS") -and
        @($migration.environment.PSObject.Properties.Name | Where-Object { $_ -like "RconSecrets__*" }).Count -eq 0) `
    -Message "The migration action must not receive RCON configuration."

$expectedPorts = @(
    "80/tcp",
    "443/tcp",
    "443/udp"
)
$actualPorts = @($caddy.ports | ForEach-Object { "$($_.published)/$($_.protocol)" } | Sort-Object)
Assert-Condition `
    -Condition (@(Compare-Object ($expectedPorts | Sort-Object) $actualPorts).Count -eq 0) `
    -Message "Caddy must be the only service publishing TCP 80 and TCP/UDP 443."

if (-not $ContractOnly) {
    Assert-Condition `
        -Condition (-not (Test-PathInsideRepository -Path $environmentPath)) `
        -Message "The deployment environment file must live outside the repository."

    $hostName = [string]$caddy.environment.GOLDSRCOPS_HOSTNAME
    $authorityValue = [string]$api.environment.Authentication__Schemes__Bearer__Authority
    $authority = $null
    Assert-Condition `
        -Condition ([Uri]::TryCreate($authorityValue, [UriKind]::Absolute, [ref]$authority) -and
            $authority.Scheme -eq [Uri]::UriSchemeHttps) `
        -Message "The authentication authority must be an absolute HTTPS URI."
    Assert-Condition `
        -Condition ($hostName -notmatch '(?i)(^|\.)example\.(com|net|org)$' -and
            $hostName -notin @("localhost", "127.0.0.1")) `
        -Message "GOLDSRCOPS_HOSTNAME still uses a placeholder or local value."
    Assert-Condition `
        -Condition ($authority.Host -notmatch '(?i)(^|\.)example\.(com|net|org)$') `
        -Message "The authentication authority still uses a placeholder host."

    $postgresPasswordFile = [string]$configuration.secrets.'postgres-password'.file
    $databaseConnectionFile = [string]$configuration.secrets.'database-connection'.file
    $rconPasswordFile = [string]$configuration.secrets.'rcon-password'.file

    Assert-SecretFile `
        -Path $postgresPasswordFile `
        -Name "PostgreSQL password" `
        -RequiredOwnerId 0
    Assert-SecretFile `
        -Path $databaseConnectionFile `
        -Name "Database connection" `
        -RequiredOwnerId 1654
    Assert-SecretFile `
        -Path $rconPasswordFile `
        -Name "RCON password" `
        -RequiredOwnerId 1654

    $databaseConnection = [System.IO.File]::ReadAllText($databaseConnectionFile).Trim()
    $rconPassword = [System.IO.File]::ReadAllText($rconPasswordFile).Trim()
    Assert-Condition `
        -Condition ($databaseConnection -notmatch '[\r\n]' -and
            $databaseConnection -match '(?i)(^|;)\s*Host\s*=\s*/var/run/postgresql\s*(;|$)' -and
            $databaseConnection -match '(?i)(^|;)\s*Password\s*=') `
        -Message "The database connection secret must be single-line, use the PostgreSQL Unix socket, and contain a password."
    Assert-Condition `
        -Condition (-not [string]::IsNullOrWhiteSpace($rconPassword) -and
            $rconPassword -notmatch '[\r\n"]') `
        -Message "The RCON password must be non-empty, single-line, and contain no double quote."

    & docker pull $api.image
    if ($LASTEXITCODE -ne 0) {
        throw "The digest-pinned API image could not be pulled."
    }

    $apiUser = ((& docker image inspect --format "{{.Config.User}}" $api.image) -join "").Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "The API image runtime user could not be inspected."
    }

    Assert-Condition `
        -Condition ($apiUser -eq "1654") `
        -Message "The API image must run as Unix UID 1654 for file-secret ownership."

    & docker run `
        --rm `
        --entrypoint /bin/sh `
        $api.image `
        -c "test -x /app/goldsrcops-migrate && test -r /app/api-entrypoint.sh"

    if ($LASTEXITCODE -ne 0) {
        throw "The API image does not contain its migration bundle and secret-loading entrypoint."
    }

    & docker run `
        --rm `
        --env "GOLDSRCOPS_ACME_EMAIL=$($caddy.environment.GOLDSRCOPS_ACME_EMAIL)" `
        --env "GOLDSRCOPS_HOSTNAME=$hostName" `
        --volume "${PSScriptRoot}/Caddyfile:/etc/caddy/Caddyfile:ro" `
        $caddy.image `
        caddy validate --config /etc/caddy/Caddyfile

    if ($LASTEXITCODE -ne 0) {
        throw "Caddy configuration validation failed with exit code $LASTEXITCODE."
    }
}

$mode = if ($ContractOnly) { "contract" } else { "deployment" }
Write-Host "Production Compose $mode preflight passed."
