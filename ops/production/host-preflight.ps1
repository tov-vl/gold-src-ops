#Requires -Version 7.0

<#
.SYNOPSIS
Audits a Linux control-plane host before the GoldSrcOps runtime is enabled.

.DESCRIPTION
Collects read-only host observations for systemd, Docker, time synchronization,
storage, UFW, listening ports, published container ports, and optional external
dependencies. The script never changes host configuration and never records
secret values. Snapshot mode exists only for deterministic CI validation and is
always marked as non-target evidence.
#>

[CmdletBinding(DefaultParameterSetName = "Live")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Live")]
    [Parameter(Mandatory = $true, ParameterSetName = "Snapshot")]
    [ValidateNotNullOrEmpty()]
    [string]$AdminIpv4Cidr,

    [Parameter(ParameterSetName = "Live")]
    [string]$EnvironmentFile,

    [Parameter(Mandatory = $true, ParameterSetName = "Snapshot")]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotFile,

    [Parameter(ParameterSetName = "Snapshot")]
    [string]$UfwStatusFile,

    [string]$EvidenceFile,

    [ValidateRange(1, 65535)]
    [int]$SshPort = 22,

    [ValidateRange(1, 1024)]
    [int]$MinimumFreeDiskGiB = 10,

    [ValidateRange(1, 100)]
    [int]$MinimumFreeInodePercent = 10,

    [switch]$RequireExternalEndpoints,

    [switch]$RequireRuntimeListeners
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$checks = [Collections.Generic.List[object]]::new()

function Add-Check {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [bool]$Passed,

        [Parameter(Mandatory = $true)]
        [string]$Detail
    )

    $checks.Add([ordered]@{
            Name = $Name
            Passed = $Passed
            Detail = $Detail
        })
}

function Invoke-NativeProbe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$Arguments = @()
    )

    if ($null -eq (Get-Command $FilePath -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{
            ExitCode = 127
            Output = ""
        }
    }

    try {
        $output = @(& $FilePath @Arguments 2>&1)
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
        }
    }
    catch {
        return [pscustomobject]@{
            ExitCode = 1
            Output = ""
        }
    }
}

function Read-EnvironmentValues {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $values = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)

    foreach ($line in [IO.File]::ReadAllLines($resolvedPath)) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }

        $separator = $trimmed.IndexOf('=')
        if ($separator -le 0) {
            throw "Deployment environment entries must use NAME=VALUE syntax."
        }

        $name = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim()
        if (-not $values.TryAdd($name, $value)) {
            throw "Deployment environment contains a duplicate setting."
        }
    }

    return $values
}

function Get-RequiredEnvironmentValue {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.Dictionary[string, string]]$Values,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $value = $null
    if (-not $Values.TryGetValue($Name, [ref]$value) -or
        [string]::IsNullOrWhiteSpace($value)) {
        throw "Deployment environment is missing '$Name'."
    }

    return $value
}

function Test-HttpsEndpoint {
    param(
        [Parameter(Mandatory = $true)]
        [Uri]$Uri
    )

    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    $response = $null
    try {
        $response = $client.GetAsync($Uri).GetAwaiter().GetResult()
        return [int]$response.StatusCode -ge 100
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $response) {
            $response.Dispose()
        }
        $client.Dispose()
    }
}

function Test-OidcMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [Uri]$Authority
    )

    $metadataUri = "{0}/.well-known/openid-configuration" -f
        $Authority.AbsoluteUri.TrimEnd('/')

    try {
        $response = Invoke-WebRequest `
            -Uri $metadataUri `
            -Method Get `
            -TimeoutSec 10 `
            -MaximumRedirection 3
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Get-StorageObservation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $space = Invoke-NativeProbe -FilePath "df" -Arguments @("-P", "-B1", "--", $Path)
    $inodes = Invoke-NativeProbe -FilePath "df" -Arguments @("-P", "-i", "--", $Path)
    if ($space.ExitCode -ne 0 -or $inodes.ExitCode -ne 0) {
        return [pscustomobject]@{
            Available = $false
            Path = $Path
            TotalBytes = 0L
            FreeBytes = 0L
            TotalInodes = 0L
            FreeInodes = 0L
        }
    }

    $spaceLine = @($space.Output -split '\r?\n' | Where-Object { $_.Trim().Length -gt 0 })[-1]
    $inodeLine = @($inodes.Output -split '\r?\n' | Where-Object { $_.Trim().Length -gt 0 })[-1]
    $spaceFields = @($spaceLine.Trim() -split '\s+')
    $inodeFields = @($inodeLine.Trim() -split '\s+')
    if ($spaceFields.Count -lt 6 -or $inodeFields.Count -lt 6) {
        return [pscustomobject]@{
            Available = $false
            Path = $Path
            TotalBytes = 0L
            FreeBytes = 0L
            TotalInodes = 0L
            FreeInodes = 0L
        }
    }

    return [pscustomobject]@{
        Available = $true
        Path = $Path
        TotalBytes = [long]$spaceFields[1]
        FreeBytes = [long]$spaceFields[3]
        TotalInodes = [long]$inodeFields[1]
        FreeInodes = [long]$inodeFields[3]
    }
}

function ConvertFrom-UfwStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Status,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedAdminCidr,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedSshPort
    )

    $rules = [Collections.Generic.List[object]]::new()
    foreach ($line in $Status -split '\r?\n') {
        $columns = @($line.Trim() -split '\s{2,}')
        if ($columns.Count -lt 3 -or $columns[1] -notmatch '\AALLOW(?:\s+IN)?\z') {
            continue
        }

        $rules.Add([pscustomobject]@{
                To = $columns[0]
                From = $columns[2]
            })
    }

    $expectedAdminAddress = $ExpectedAdminCidr.Substring(
        0,
        $ExpectedAdminCidr.LastIndexOf('/'))
    $isAnySource = {
        param([string]$Source)

        return $Source -match '\A(?:Anywhere(?:\s+\(v6\))?|0\.0\.0\.0/0|::/0)\z'
    }
    $matchesPort = {
        param(
            [object]$Rule,
            [int]$Port,
            [string]$Protocol
        )

        return $Rule.To -match ("\A{0}/{1}(?:\s|\z)" -f $Port, $Protocol)
    }

    $sshRules = @($rules | Where-Object { & $matchesPort $_ $ExpectedSshPort "tcp" })
    $restrictedSsh = @($sshRules | Where-Object {
            $_.From -eq $ExpectedAdminCidr -or $_.From -eq $expectedAdminAddress
        }).Count -gt 0
    $unrestrictedSsh = @($sshRules | Where-Object { & $isAnySource $_.From }).Count -gt 0
    $httpAllowed = @($rules | Where-Object {
            (& $matchesPort $_ 80 "tcp") -and (& $isAnySource $_.From)
        }).Count -gt 0
    $httpsTcpAllowed = @($rules | Where-Object {
            (& $matchesPort $_ 443 "tcp") -and (& $isAnySource $_.From)
        }).Count -gt 0
    $httpsUdpAllowed = @($rules | Where-Object {
            (& $matchesPort $_ 443 "udp") -and (& $isAnySource $_.From)
        }).Count -gt 0
    $postgresAllowed = @($rules | Where-Object {
            $_.To -match '\A5432(?:/tcp)?(?:\s|\z)'
        }).Count -gt 0

    return [pscustomobject]@{
        Provider = "ufw"
        Active = $Status -match '(?m)^Status:\s+active\s*$'
        DefaultDenyIncoming = $Status -match '(?m)^Default:\s+deny\s+\(incoming\)'
        DefaultAllowOutgoing = $Status -match '(?m)^Default:.*allow\s+\(outgoing\)'
        SshRestricted = $restrictedSsh
        SshUnrestricted = $unrestrictedSsh
        HttpAllowed = $httpAllowed
        HttpsTcpAllowed = $httpsTcpAllowed
        HttpsUdpAllowed = $httpsUdpAllowed
        PostgresAllowed = $postgresAllowed
    }
}

function Get-PublicListenerPorts {
    $probe = Invoke-NativeProbe -FilePath "ss" -Arguments @("-H", "-lntu")
    if ($probe.ExitCode -ne 0) {
        return [pscustomobject]@{
            Available = $false
            Ports = @()
        }
    }

    $ports = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $probe.Output -split '\r?\n') {
        $fields = @($line.Trim() -split '\s+')
        if ($fields.Count -lt 5) {
            continue
        }

        $protocol = $fields[0].ToLowerInvariant()
        $localEndpoint = $fields[4]
        $separator = $localEndpoint.LastIndexOf(':')
        if ($separator -lt 0) {
            continue
        }

        $address = $localEndpoint.Substring(0, $separator).Trim('[', ']')
        $port = $localEndpoint.Substring($separator + 1)
        if ($port -notmatch '\A\d+\z' -or
            $address -eq "::1" -or
            $address -match '\A127\.') {
            continue
        }

        [void]$ports.Add("$protocol/$port")
    }

    return [pscustomobject]@{
        Available = $true
        Ports = @($ports | Sort-Object)
    }
}

function Get-DockerPublishedPorts {
    $probe = Invoke-NativeProbe -FilePath "docker" -Arguments @("ps", "--format", "{{.Ports}}")
    if ($probe.ExitCode -ne 0) {
        return [pscustomobject]@{
            Available = $false
            Ports = @()
        }
    }

    $ports = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pattern = '(?<hostPort>\d+)(?:-\d+)?->\d+(?:-\d+)?/(?<protocol>tcp|udp)'
    foreach ($match in [regex]::Matches($probe.Output, $pattern)) {
        [void]$ports.Add("$($match.Groups['protocol'].Value)/$($match.Groups['hostPort'].Value)")
    }

    return [pscustomobject]@{
        Available = $true
        Ports = @($ports | Sort-Object)
    }
}

function Get-LiveObservation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedAdminCidr,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedSshPort,

        [string]$DeploymentEnvironmentFile
    )

    if (-not $IsLinux) {
        throw "Live host preflight is supported only on Linux."
    }

    $uidProbe = Invoke-NativeProbe -FilePath "id" -Arguments @("-u")
    $architectureProbe = Invoke-NativeProbe -FilePath "uname" -Arguments @("-m")
    $operatingSystem = "Linux"
    if (Test-Path -LiteralPath "/etc/os-release" -PathType Leaf) {
        $prettyName = Get-Content -LiteralPath "/etc/os-release" |
            Where-Object { $_ -match '\APRETTY_NAME=' } |
            Select-Object -First 1
        if ($null -ne $prettyName) {
            $operatingSystem = $prettyName.Substring($prettyName.IndexOf('=') + 1).Trim('"')
        }
    }

    $dockerCliAvailable = $null -ne (Get-Command "docker" -ErrorAction SilentlyContinue)
    $dockerInfoProbe = Invoke-NativeProbe `
        -FilePath "docker" `
        -Arguments @("info", "--format", "{{json .}}")
    $dockerInfo = $null
    if ($dockerInfoProbe.ExitCode -eq 0) {
        $dockerInfo = $dockerInfoProbe.Output | ConvertFrom-Json -Depth 100
    }

    $composeProbe = Invoke-NativeProbe `
        -FilePath "docker" `
        -Arguments @("compose", "version", "--short")
    $dockerActiveProbe = Invoke-NativeProbe `
        -FilePath "systemctl" `
        -Arguments @("is-active", "docker.service")
    $dockerEnabledProbe = Invoke-NativeProbe `
        -FilePath "systemctl" `
        -Arguments @("is-enabled", "docker.service")
    $timeProbe = Invoke-NativeProbe `
        -FilePath "timedatectl" `
        -Arguments @("show", "--property=NTPSynchronized", "--value")

    $dockerRoot = if ($null -ne $dockerInfo -and
        -not [string]::IsNullOrWhiteSpace([string]$dockerInfo.DockerRootDir)) {
        [string]$dockerInfo.DockerRootDir
    }
    else {
        "/var/lib/docker"
    }
    $storage = Get-StorageObservation -Path $dockerRoot

    $previousLocale = $env:LC_ALL
    try {
        $env:LC_ALL = "C"
        $ufwProbe = Invoke-NativeProbe -FilePath "ufw" -Arguments @("status", "verbose")
    }
    finally {
        $env:LC_ALL = $previousLocale
    }

    $firewall = if ($ufwProbe.ExitCode -eq 0) {
        ConvertFrom-UfwStatus `
            -Status $ufwProbe.Output `
            -ExpectedAdminCidr $ExpectedAdminCidr `
            -ExpectedSshPort $ExpectedSshPort
    }
    else {
        [pscustomobject]@{
            Provider = "ufw"
            Active = $false
            DefaultDenyIncoming = $false
            DefaultAllowOutgoing = $false
            SshRestricted = $false
            SshUnrestricted = $false
            HttpAllowed = $false
            HttpsTcpAllowed = $false
            HttpsUdpAllowed = $false
            PostgresAllowed = $false
        }
    }

    $listeners = Get-PublicListenerPorts
    $publishedPorts = Get-DockerPublishedPorts

    $ghcrReachable = $null
    $backupEndpointReachable = $null
    $oidcMetadataReachable = $null
    if (-not [string]::IsNullOrWhiteSpace($DeploymentEnvironmentFile)) {
        $values = Read-EnvironmentValues -Path $DeploymentEnvironmentFile
        $image = Get-RequiredEnvironmentValue -Values $values -Name "GOLDSRCOPS_IMAGE"
        $backupRepository = Get-RequiredEnvironmentValue `
            -Values $values `
            -Name "GOLDSRCOPS_BACKUP_REPOSITORY"
        $authorityValue = Get-RequiredEnvironmentValue `
            -Values $values `
            -Name "GOLDSRCOPS_AUTHENTICATION_AUTHORITY"

        $registrySeparator = $image.IndexOf('/')
        if ($registrySeparator -le 0) {
            throw "GOLDSRCOPS_IMAGE must include a registry host."
        }

        $backupUri = $null
        $authority = $null
        if (-not $backupRepository.StartsWith("s3:https://", [StringComparison]::OrdinalIgnoreCase) -or
            -not [Uri]::TryCreate($backupRepository.Substring(3), [UriKind]::Absolute, [ref]$backupUri)) {
            throw "GOLDSRCOPS_BACKUP_REPOSITORY must use an S3-compatible HTTPS endpoint."
        }
        if (-not [Uri]::TryCreate($authorityValue, [UriKind]::Absolute, [ref]$authority) -or
            $authority.Scheme -ne [Uri]::UriSchemeHttps) {
            throw "GOLDSRCOPS_AUTHENTICATION_AUTHORITY must be an absolute HTTPS URI."
        }

        $registryUri = [Uri]("https://{0}/v2/" -f $image.Substring(0, $registrySeparator))
        $ghcrReachable = Test-HttpsEndpoint -Uri $registryUri
        $backupEndpointReachable = Test-HttpsEndpoint -Uri $backupUri
        $oidcMetadataReachable = Test-OidcMetadata -Authority $authority
    }

    return [pscustomobject]@{
        SchemaVersion = 1
        CapturedAtUtc = [DateTimeOffset]::UtcNow
        IsLinux = $true
        OperatingSystem = $operatingSystem
        Architecture = $architectureProbe.Output.Trim()
        EffectiveUserId = if ($uidProbe.ExitCode -eq 0) { [int]$uidProbe.Output.Trim() } else { -1 }
        DockerCliAvailable = $dockerCliAvailable
        DockerEngineReachable = $null -ne $dockerInfo
        DockerServerVersion = if ($null -ne $dockerInfo) { [string]$dockerInfo.ServerVersion } else { "" }
        DockerComposeAvailable = $composeProbe.ExitCode -eq 0
        DockerComposeVersion = if ($composeProbe.ExitCode -eq 0) { $composeProbe.Output.Trim() } else { "" }
        DockerServiceActive = $dockerActiveProbe.ExitCode -eq 0 -and
            $dockerActiveProbe.Output.Trim() -eq "active"
        DockerServiceEnabled = $dockerEnabledProbe.ExitCode -eq 0 -and
            $dockerEnabledProbe.Output.Trim() -eq "enabled"
        TimeSynchronized = $timeProbe.ExitCode -eq 0 -and
            $timeProbe.Output.Trim().Equals("yes", [StringComparison]::OrdinalIgnoreCase)
        StorageAvailable = $storage.Available
        StoragePath = $storage.Path
        StorageTotalBytes = $storage.TotalBytes
        StorageFreeBytes = $storage.FreeBytes
        StorageTotalInodes = $storage.TotalInodes
        StorageFreeInodes = $storage.FreeInodes
        FirewallProvider = $firewall.Provider
        FirewallActive = $firewall.Active
        FirewallDefaultDenyIncoming = $firewall.DefaultDenyIncoming
        FirewallDefaultAllowOutgoing = $firewall.DefaultAllowOutgoing
        FirewallSshRestricted = $firewall.SshRestricted
        FirewallSshUnrestricted = $firewall.SshUnrestricted
        FirewallHttpAllowed = $firewall.HttpAllowed
        FirewallHttpsTcpAllowed = $firewall.HttpsTcpAllowed
        FirewallHttpsUdpAllowed = $firewall.HttpsUdpAllowed
        FirewallPostgresAllowed = $firewall.PostgresAllowed
        ListenerInspectionAvailable = $listeners.Available
        PublicListenerPorts = @($listeners.Ports)
        DockerPortInspectionAvailable = $publishedPorts.Available
        DockerPublishedPorts = @($publishedPorts.Ports)
        GhcrReachable = $ghcrReachable
        BackupEndpointReachable = $backupEndpointReachable
        OidcMetadataReachable = $oidcMetadataReachable
    }
}

function Test-PathInsideRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $repoRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    return $fullPath.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)
}

function Write-HostEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Evidence,

        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (Test-PathInsideRepository -Path $fullPath) {
        throw "Operational evidence must be written outside the repository."
    }

    $directory = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $directory)
    }

    $temporaryPath = "$fullPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $Evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
        if ($IsLinux) {
            $chmod = Invoke-NativeProbe -FilePath "chmod" -Arguments @("0600", "--", $temporaryPath)
            if ($chmod.ExitCode -ne 0) {
                throw "Could not restrict host-readiness evidence permissions."
            }
        }
        Move-Item -LiteralPath $temporaryPath -Destination $fullPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

if ($AdminIpv4Cidr -notmatch '\A(?<address>\d{1,3}(?:\.\d{1,3}){3})/32\z') {
    throw "AdminIpv4Cidr must identify exactly one IPv4 address with a /32 prefix."
}

$parsedAddress = $null
if (-not [Net.IPAddress]::TryParse($Matches["address"], [ref]$parsedAddress) -or
    $parsedAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw "AdminIpv4Cidr must contain a valid IPv4 address."
}

if ($PSCmdlet.ParameterSetName -eq "Live") {
    if ($RequireExternalEndpoints -and [string]::IsNullOrWhiteSpace($EnvironmentFile)) {
        throw "EnvironmentFile is required when external endpoint checks are enabled."
    }

    $deploymentEnvironmentForProbe = if ($RequireExternalEndpoints) {
        $EnvironmentFile
    }
    else {
        $null
    }
    $observation = Get-LiveObservation `
        -ExpectedAdminCidr $AdminIpv4Cidr `
        -ExpectedSshPort $SshPort `
        -DeploymentEnvironmentFile $deploymentEnvironmentForProbe
    $source = "Live"
    $targetEvidence = $true
}
else {
    $resolvedSnapshot = (Resolve-Path -LiteralPath $SnapshotFile).Path
    $observation = Get-Content -LiteralPath $resolvedSnapshot -Raw |
        ConvertFrom-Json -Depth 100
    if (-not [string]::IsNullOrWhiteSpace($UfwStatusFile)) {
        $resolvedUfwStatus = (Resolve-Path -LiteralPath $UfwStatusFile).Path
        $firewall = ConvertFrom-UfwStatus `
            -Status (Get-Content -LiteralPath $resolvedUfwStatus -Raw) `
            -ExpectedAdminCidr $AdminIpv4Cidr `
            -ExpectedSshPort $SshPort
        $observation.FirewallProvider = $firewall.Provider
        $observation.FirewallActive = $firewall.Active
        $observation.FirewallDefaultDenyIncoming = $firewall.DefaultDenyIncoming
        $observation.FirewallDefaultAllowOutgoing = $firewall.DefaultAllowOutgoing
        $observation.FirewallSshRestricted = $firewall.SshRestricted
        $observation.FirewallSshUnrestricted = $firewall.SshUnrestricted
        $observation.FirewallHttpAllowed = $firewall.HttpAllowed
        $observation.FirewallHttpsTcpAllowed = $firewall.HttpsTcpAllowed
        $observation.FirewallHttpsUdpAllowed = $firewall.HttpsUdpAllowed
        $observation.FirewallPostgresAllowed = $firewall.PostgresAllowed
    }
    $source = "Snapshot"
    $targetEvidence = $false
}

if ([int]$observation.SchemaVersion -ne 1) {
    throw "Unsupported host observation schema version."
}

$architectureReady = [string]$observation.Architecture -in @("x86_64", "amd64")
Add-Check -Name "Linux host" -Passed ([bool]$observation.IsLinux) -Detail $(
    if ([bool]$observation.IsLinux) { "Linux detected." } else { "Linux is required." })
Add-Check -Name "Privileged audit" -Passed ([int]$observation.EffectiveUserId -eq 0) -Detail $(
    if ([int]$observation.EffectiveUserId -eq 0) { "Audit is running as root." } else { "Run the live audit as root." })
Add-Check -Name "Host architecture" -Passed $architectureReady -Detail $(
    if ($architectureReady) { "The host uses a supported x86-64 architecture." } else { "The host must use x86-64." })
Add-Check -Name "Docker CLI" -Passed ([bool]$observation.DockerCliAvailable) -Detail $(
    if ([bool]$observation.DockerCliAvailable) { "Docker CLI is available." } else { "Docker CLI is unavailable." })
Add-Check -Name "Docker engine" -Passed ([bool]$observation.DockerEngineReachable) -Detail $(
    if ([bool]$observation.DockerEngineReachable) { "Docker engine is reachable." } else { "Docker engine is unreachable." })
Add-Check -Name "Docker Compose" -Passed ([bool]$observation.DockerComposeAvailable) -Detail $(
    if ([bool]$observation.DockerComposeAvailable) { "Docker Compose is available." } else { "Docker Compose is unavailable." })
Add-Check -Name "Docker service active" -Passed ([bool]$observation.DockerServiceActive) -Detail $(
    if ([bool]$observation.DockerServiceActive) { "docker.service is active." } else { "docker.service is not active." })
Add-Check -Name "Docker service enabled" -Passed ([bool]$observation.DockerServiceEnabled) -Detail $(
    if ([bool]$observation.DockerServiceEnabled) { "docker.service is enabled at boot." } else { "docker.service is not enabled at boot." })
Add-Check -Name "Time synchronization" -Passed ([bool]$observation.TimeSynchronized) -Detail $(
    if ([bool]$observation.TimeSynchronized) { "The host clock is synchronized." } else { "The host clock is not synchronized." })

$minimumFreeBytes = [long]$MinimumFreeDiskGiB * 1GB
$storageReady = [bool]$observation.StorageAvailable -and
    [long]$observation.StorageFreeBytes -ge $minimumFreeBytes
$freeDiskGiB = [Math]::Round([long]$observation.StorageFreeBytes / 1GB, 2)
Add-Check -Name "Free disk" -Passed $storageReady -Detail $(
    if ($storageReady) { "$freeDiskGiB GiB is free in the Docker storage filesystem." } else { "Docker storage has less than $MinimumFreeDiskGiB GiB free." })

$freeInodePercent = if ([long]$observation.StorageTotalInodes -gt 0) {
    [Math]::Floor(
        ([long]$observation.StorageFreeInodes * 100) /
        [long]$observation.StorageTotalInodes)
}
else {
    0
}
$inodesReady = [bool]$observation.StorageAvailable -and
    $freeInodePercent -ge $MinimumFreeInodePercent
Add-Check -Name "Free inodes" -Passed $inodesReady -Detail $(
    if ($inodesReady) { "$freeInodePercent percent of inodes are free." } else { "Fewer than $MinimumFreeInodePercent percent of inodes are free." })

Add-Check -Name "UFW active" -Passed (
    [string]$observation.FirewallProvider -eq "ufw" -and [bool]$observation.FirewallActive) -Detail $(
    if ([bool]$observation.FirewallActive) { "UFW is active." } else { "The UFW reference firewall is not active." })
Add-Check -Name "Default inbound policy" -Passed ([bool]$observation.FirewallDefaultDenyIncoming) -Detail $(
    if ([bool]$observation.FirewallDefaultDenyIncoming) { "Inbound traffic is denied by default." } else { "Inbound traffic is not denied by default." })
Add-Check -Name "Default outbound policy" -Passed ([bool]$observation.FirewallDefaultAllowOutgoing) -Detail $(
    if ([bool]$observation.FirewallDefaultAllowOutgoing) { "Outbound traffic is allowed by default." } else { "The reference outbound policy is not active." })
$sshFirewallReady = [bool]$observation.FirewallSshRestricted -and
    -not [bool]$observation.FirewallSshUnrestricted
Add-Check -Name "Restricted SSH" -Passed $sshFirewallReady -Detail $(
    if ($sshFirewallReady) { "SSH is allowed only from the expected IPv4 /32." } else { "SSH is missing its /32 rule or is allowed from an unrestricted source." })
$publicHttpsFirewallReady = [bool]$observation.FirewallHttpAllowed -and
    [bool]$observation.FirewallHttpsTcpAllowed -and
    [bool]$observation.FirewallHttpsUdpAllowed
Add-Check -Name "Public HTTPS firewall" -Passed $publicHttpsFirewallReady -Detail $(
    if ($publicHttpsFirewallReady) { "TCP 80, TCP 443, and UDP 443 are allowed for Caddy." } else { "The Caddy firewall rules are incomplete." })
Add-Check -Name "PostgreSQL firewall" -Passed (-not [bool]$observation.FirewallPostgresAllowed) -Detail $(
    if (-not [bool]$observation.FirewallPostgresAllowed) { "No inbound PostgreSQL allow rule exists." } else { "An inbound PostgreSQL allow rule exists." })

$publicListenerPorts = @($observation.PublicListenerPorts | ForEach-Object { [string]$_ })
$forbiddenPublicPorts = @(
    "tcp/3000",
    "tcp/4317",
    "tcp/4318",
    "tcp/5432",
    "tcp/8080",
    "tcp/9090"
)
$exposedForbiddenPorts = @($publicListenerPorts | Where-Object { $_ -in $forbiddenPublicPorts })
Add-Check -Name "Listener inspection" -Passed ([bool]$observation.ListenerInspectionAvailable) -Detail $(
    if ([bool]$observation.ListenerInspectionAvailable) { "Public listeners were inspected." } else { "Public listeners could not be inspected." })
Add-Check -Name "Private service listeners" -Passed ($exposedForbiddenPorts.Count -eq 0) -Detail $(
    if ($exposedForbiddenPorts.Count -eq 0) { "No database, API, or telemetry port is publicly listening." } else { "A database, API, or telemetry port is publicly listening." })
Add-Check -Name "SSH listener" -Passed ($publicListenerPorts -contains "tcp/$SshPort") -Detail $(
    if ($publicListenerPorts -contains "tcp/$SshPort") { "The expected SSH listener is present." } else { "The expected SSH listener is absent." })

$publishedPorts = @($observation.DockerPublishedPorts | ForEach-Object { [string]$_ })
$allowedPublishedPorts = @("tcp/80", "tcp/443", "udp/443")
$unexpectedPublishedPorts = @($publishedPorts | Where-Object { $_ -notin $allowedPublishedPorts })
Add-Check -Name "Docker port inspection" -Passed ([bool]$observation.DockerPortInspectionAvailable) -Detail $(
    if ([bool]$observation.DockerPortInspectionAvailable) { "Docker published ports were inspected." } else { "Docker published ports could not be inspected." })
Add-Check -Name "Docker published ports" -Passed ($unexpectedPublishedPorts.Count -eq 0) -Detail $(
    if ($unexpectedPublishedPorts.Count -eq 0) { "Only the Caddy port set is eligible for publication." } else { "A container publishes a port outside the Caddy port set." })

if ($RequireRuntimeListeners) {
    $runtimePortsReady = @($allowedPublishedPorts | Where-Object {
            $publicListenerPorts -notcontains $_ -or $publishedPorts -notcontains $_
        }).Count -eq 0
    Add-Check -Name "Runtime listeners" -Passed $runtimePortsReady -Detail $(
        if ($runtimePortsReady) { "Caddy publishes and listens on its complete port set." } else { "The Caddy runtime port set is incomplete." })
}

if ($RequireExternalEndpoints) {
    Add-Check -Name "GHCR connectivity" -Passed ([bool]$observation.GhcrReachable) -Detail $(
        if ([bool]$observation.GhcrReachable) { "The registry HTTPS endpoint is reachable." } else { "The registry HTTPS endpoint is unreachable." })
    Add-Check -Name "Backup connectivity" -Passed ([bool]$observation.BackupEndpointReachable) -Detail $(
        if ([bool]$observation.BackupEndpointReachable) { "The backup HTTPS endpoint is reachable." } else { "The backup HTTPS endpoint is unreachable." })
    Add-Check -Name "OIDC metadata" -Passed ([bool]$observation.OidcMetadataReachable) -Detail $(
        if ([bool]$observation.OidcMetadataReachable) { "OIDC metadata is reachable over HTTPS." } else { "OIDC metadata is unreachable or invalid." })
}

$failedChecks = @($checks | Where-Object { -not $_.Passed })
$result = if ($failedChecks.Count -eq 0) { "Passed" } else { "Failed" }
$evidence = [ordered]@{
    SchemaVersion = 1
    CheckedAtUtc = [DateTimeOffset]::UtcNow
    Source = $source
    TargetEvidence = $targetEvidence
    Result = $result
    Host = [ordered]@{
        OperatingSystem = [string]$observation.OperatingSystem
        Architecture = [string]$observation.Architecture
        DockerServerVersion = [string]$observation.DockerServerVersion
        DockerComposeVersion = [string]$observation.DockerComposeVersion
        FreeDiskGiB = $freeDiskGiB
        FreeInodePercent = $freeInodePercent
        FirewallProvider = [string]$observation.FirewallProvider
        PublicListenerPorts = $publicListenerPorts
        DockerPublishedPorts = $publishedPorts
    }
    Checks = @($checks)
}

Write-HostEvidence -Evidence $evidence -Path $EvidenceFile

foreach ($check in $checks) {
    $marker = if ($check.Passed) { "PASS" } else { "FAIL" }
    Write-Host "[$marker] $($check.Name): $($check.Detail)"
}

if ($failedChecks.Count -gt 0) {
    $failedNames = ($failedChecks | ForEach-Object { $_.Name }) -join ", "
    throw "Host-readiness preflight failed: $failedNames."
}

Write-Host "Host-readiness preflight passed."
