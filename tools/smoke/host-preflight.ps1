#Requires -Version 7.0

<#
.SYNOPSIS
Exercises the host-readiness validator with deterministic snapshots.

.DESCRIPTION
Runs one passing snapshot and focused failing snapshots for service startup,
time, storage, firewall, SSH hardening, public ports, and external dependency
checks. Snapshot evidence is verified as non-target evidence and all temporary
files are removed.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$preflightScript = Join-Path $repoRoot "ops/production/host-preflight.ps1"
$runId = [Guid]::NewGuid().ToString("N")
$smokeDirectory = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-host-preflight-$runId"

function New-ReadySnapshot {
    return [ordered]@{
        SchemaVersion = 2
        CapturedAtUtc = [DateTimeOffset]::UtcNow
        IsLinux = $true
        OperatingSystem = "Ubuntu 24.04 LTS"
        Architecture = "x86_64"
        EffectiveUserId = 0
        DockerCliAvailable = $true
        DockerEngineReachable = $true
        DockerServerVersion = "28.0.0"
        DockerComposeAvailable = $true
        DockerComposeVersion = "2.35.0"
        DockerServiceActive = $true
        DockerServiceEnabled = $true
        TimeSynchronized = $true
        StorageAvailable = $true
        StoragePath = "/var/lib/docker"
        StorageTotalBytes = 60GB
        StorageFreeBytes = 30GB
        StorageTotalInodes = 1000000L
        StorageFreeInodes = 750000L
        FirewallProvider = "ufw"
        FirewallActive = $true
        FirewallDefaultDenyIncoming = $true
        FirewallDefaultAllowOutgoing = $true
        FirewallSshRestricted = $true
        FirewallSshUnrestricted = $false
        FirewallHttpAllowed = $true
        FirewallHttpsTcpAllowed = $true
        FirewallHttpsUdpAllowed = $true
        FirewallPostgresAllowed = $false
        SshConfigurationAvailable = $true
        SshRootLoginDisabled = $true
        SshPasswordAuthenticationDisabled = $true
        SshKeyboardInteractiveAuthenticationDisabled = $true
        SshPublicKeyAuthenticationEnabled = $true
        SshPublicKeyOnly = $true
        SshExpectedUserAllowed = $true
        ListenerInspectionAvailable = $true
        PublicListenerPorts = @("tcp/22", "tcp/80", "tcp/443", "udp/443")
        DockerPortInspectionAvailable = $true
        DockerPublishedPorts = @("tcp/80", "tcp/443", "udp/443")
        GhcrReachable = $true
        BackupEndpointReachable = $true
        OidcMetadataReachable = $true
    }
}

function Invoke-SnapshotCase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$Snapshot,

        [Parameter(Mandatory = $true)]
        [bool]$ShouldPass,

        [string]$UfwStatus
    )

    $safeName = $Name.ToLowerInvariant() -replace '[^a-z0-9]+', '-'
    $snapshotFile = Join-Path $smokeDirectory "$safeName-snapshot.json"
    $evidenceFile = Join-Path $smokeDirectory "$safeName-evidence.json"
    $ufwStatusFile = Join-Path $smokeDirectory "$safeName-ufw.txt"
    $Snapshot | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $snapshotFile -Encoding utf8NoBOM

    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @(
            "-NoLogo",
            "-NoProfile",
            "-File",
            $preflightScript,
            "-AdminIpv4Cidr",
            "203.0.113.10/32",
            "-OperatorUser",
            "gsoadmin",
            "-SnapshotFile",
            $snapshotFile,
            "-EvidenceFile",
            $evidenceFile,
            "-RequireExternalEndpoints",
            "-RequireRuntimeListeners"
        )) {
        $arguments.Add([string]$argument)
    }
    if (-not [string]::IsNullOrWhiteSpace($UfwStatus)) {
        Set-Content -LiteralPath $ufwStatusFile -Value $UfwStatus -Encoding utf8NoBOM
        $arguments.Add("-UfwStatusFile")
        $arguments.Add($ufwStatusFile)
    }

    $output = @(& pwsh @arguments 2>&1)
    $exitCode = $LASTEXITCODE

    if ($ShouldPass -and $exitCode -ne 0) {
        throw "Host-preflight case '$Name' failed unexpectedly: $($output -join [Environment]::NewLine)"
    }
    if (-not $ShouldPass -and $exitCode -eq 0) {
        throw "Host-preflight case '$Name' passed unexpectedly."
    }
    if (-not (Test-Path -LiteralPath $evidenceFile -PathType Leaf)) {
        throw "Host-preflight case '$Name' did not write evidence."
    }

    $evidenceText = Get-Content -LiteralPath $evidenceFile -Raw
    $evidence = $evidenceText | ConvertFrom-Json -Depth 100
    if ([int]$evidence.SchemaVersion -ne 2) {
        throw "Host-preflight evidence uses an unexpected schema version."
    }
    if ($evidence.Source -ne "Snapshot" -or [bool]$evidence.TargetEvidence) {
        throw "Snapshot evidence could be mistaken for live target evidence."
    }
    if ($ShouldPass -and $evidence.Result -ne "Passed") {
        throw "Passing host-preflight evidence did not record Passed."
    }
    if (-not $ShouldPass -and $evidence.Result -ne "Failed") {
        throw "Failing host-preflight evidence did not record Failed."
    }
    if ($evidenceText -match '(?i)password|secret|authorization|203\.0\.113\.') {
        throw "Host-preflight evidence contains a forbidden sensitive marker."
    }

    Write-Host "Host-preflight case '$Name' behaved as expected."
}

try {
    [void](New-Item -ItemType Directory -Path $smokeDirectory)

    $readyUfwStatus = @'
Status: active
Logging: on (low)
Default: deny (incoming), allow (outgoing), disabled (routed)

To                         Action      From
--                         ------      ----
22/tcp                     ALLOW IN    203.0.113.10
80/tcp                     ALLOW IN    Anywhere
443/tcp                    ALLOW IN    Anywhere
443/udp                    ALLOW IN    Anywhere
'@

    Invoke-SnapshotCase `
        -Name "ready" `
        -Snapshot (New-ReadySnapshot) `
        -ShouldPass $true `
        -UfwStatus $readyUfwStatus

    $dockerDisabled = New-ReadySnapshot
    $dockerDisabled["DockerServiceEnabled"] = $false
    Invoke-SnapshotCase `
        -Name "docker disabled" `
        -Snapshot $dockerDisabled `
        -ShouldPass $false

    $timeUnsynchronized = New-ReadySnapshot
    $timeUnsynchronized["TimeSynchronized"] = $false
    Invoke-SnapshotCase `
        -Name "time unsynchronized" `
        -Snapshot $timeUnsynchronized `
        -ShouldPass $false

    $lowDisk = New-ReadySnapshot
    $lowDisk["StorageFreeBytes"] = 1GB
    Invoke-SnapshotCase `
        -Name "low disk" `
        -Snapshot $lowDisk `
        -ShouldPass $false

    $unrestrictedSsh = New-ReadySnapshot
    $unrestrictedSshUfwStatus = $readyUfwStatus.Replace(
        "22/tcp                     ALLOW IN    203.0.113.10",
        "22/tcp                     ALLOW IN    Anywhere")
    Invoke-SnapshotCase `
        -Name "unrestricted ssh" `
        -Snapshot $unrestrictedSsh `
        -ShouldPass $false `
        -UfwStatus $unrestrictedSshUfwStatus

    $rootSshLogin = New-ReadySnapshot
    $rootSshLogin["SshRootLoginDisabled"] = $false
    Invoke-SnapshotCase `
        -Name "root SSH login" `
        -Snapshot $rootSshLogin `
        -ShouldPass $false

    $interactiveSsh = New-ReadySnapshot
    $interactiveSsh["SshPasswordAuthenticationDisabled"] = $false
    Invoke-SnapshotCase `
        -Name "interactive SSH authentication" `
        -Snapshot $interactiveSsh `
        -ShouldPass $false

    $operatorNotAllowed = New-ReadySnapshot
    $operatorNotAllowed["SshExpectedUserAllowed"] = $false
    Invoke-SnapshotCase `
        -Name "operator not allowed" `
        -Snapshot $operatorNotAllowed `
        -ShouldPass $false

    $publicPostgres = New-ReadySnapshot
    $publicPostgres["PublicListenerPorts"] = @(
        "tcp/22",
        "tcp/80",
        "tcp/443",
        "tcp/5432",
        "udp/443"
    )
    $publicPostgresUfwStatus = "$readyUfwStatus`n5432/tcp                   ALLOW IN    Anywhere"
    Invoke-SnapshotCase `
        -Name "public postgres" `
        -Snapshot $publicPostgres `
        -ShouldPass $false `
        -UfwStatus $publicPostgresUfwStatus

    $publicDockerApi = New-ReadySnapshot
    $publicDockerApi["PublicListenerPorts"] = @(
        "tcp/22",
        "tcp/80",
        "tcp/443",
        "tcp/2375",
        "udp/443"
    )
    Invoke-SnapshotCase `
        -Name "public Docker API" `
        -Snapshot $publicDockerApi `
        -ShouldPass $false

    $unexpectedDockerPort = New-ReadySnapshot
    $unexpectedDockerPort["DockerPublishedPorts"] = @(
        "tcp/80",
        "tcp/443",
        "tcp/5432",
        "udp/443"
    )
    Invoke-SnapshotCase `
        -Name "unexpected Docker port" `
        -Snapshot $unexpectedDockerPort `
        -ShouldPass $false

    $oidcUnavailable = New-ReadySnapshot
    $oidcUnavailable["OidcMetadataReachable"] = $false
    Invoke-SnapshotCase `
        -Name "OIDC unavailable" `
        -Snapshot $oidcUnavailable `
        -ShouldPass $false

    Write-Host "Host-readiness preflight smoke passed."
}
finally {
    if (Test-Path -LiteralPath $smokeDirectory) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedSmokeDirectory = [IO.Path]::GetFullPath($smokeDirectory)
        if (-not $resolvedSmokeDirectory.StartsWith(
                $temporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a host-preflight smoke directory outside the system temporary path."
        }

        Remove-Item -LiteralPath $resolvedSmokeDirectory -Recurse -Force
    }
}
