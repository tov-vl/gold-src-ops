#Requires -Version 7.0

<#
.SYNOPSIS
Builds and smoke-tests the production GoldSrcOps Web container image.

.DESCRIPTION
Verifies immutable-image metadata when supplied, the minimal non-root runtime,
the built-in liveness check, hardened container settings, and the public
dashboard's safe unavailable state when its API dependency cannot be reached.

.PARAMETER StartupTimeoutSeconds
How long to wait for the Web health endpoint and container health check.

.PARAMETER KeepImage
Keeps a locally built smoke image for troubleshooting.

.PARAMETER ImageReference
Pulls and tests an existing Web image by immutable sha256 digest.

.PARAMETER ExpectedImageSource
Expected org.opencontainers.image.source label for an existing image.

.PARAMETER ExpectedImageRevision
Expected org.opencontainers.image.revision label for an existing image.

.PARAMETER ExpectedImageVersion
Expected org.opencontainers.image.version label for an existing image.
#>

[CmdletBinding()]
param(
    [ValidateRange(10, 300)]
    [int]$StartupTimeoutSeconds = 90,

    [switch]$KeepImage,

    [string]$ImageReference,

    [string]$ExpectedImageSource,

    [string]$ExpectedImageRevision,

    [string]$ExpectedImageVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$buildImageLocally = [string]::IsNullOrWhiteSpace($ImageReference)
$imageTag = if ($buildImageLocally) { "goldsrcops-web:smoke-$runId" } else { $ImageReference }
$containerName = "goldsrcops-smoke-web-$runId"
$imageBuilt = $false
$succeeded = $false

if (-not $buildImageLocally -and
    $ImageReference -notmatch '\A[^@\s]+@sha256:[0-9a-f]{64}\z') {
    throw "ImageReference must use an immutable sha256 digest."
}

function Write-Step {
    param([string]$Name)

    Write-Host ""
    Write-Host "==> $Name"
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-ExternalCapture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = @(& $FilePath @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
    }

    return ($output -join [Environment]::NewLine).Trim()
}

function Remove-ContainerIfPresent {
    & docker container inspect $containerName *> $null
    if ($LASTEXITCODE -eq 0) {
        & docker rm --force $containerName *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Could not remove container '$containerName'."
        }
    }
}

function Remove-ImageIfPresent {
    & docker image inspect $imageTag *> $null
    if ($LASTEXITCODE -eq 0) {
        & docker image rm --force $imageTag *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Could not remove image '$imageTag'."
        }
    }
}

function Wait-HttpStatus {
    param(
        [Parameter(Mandatory = $true)]
        [Uri]$Uri,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                return $response
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Web endpoint did not return HTTP 200 within $TimeoutSeconds seconds."
}

function Wait-ContainerHealthy {
    param([int]$TimeoutSeconds)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $status = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "inspect",
            "--format",
            "{{.State.Health.Status}}",
            $containerName)
        if ($status -eq "healthy") {
            return
        }

        if ($status -eq "unhealthy") {
            throw "Web container health check reported unhealthy."
        }

        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Web container did not become healthy within $TimeoutSeconds seconds."
}

Push-Location -LiteralPath $repoRoot
try {
    Write-Step "Check Docker"
    $dockerVersion = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "info",
        "--format",
        "{{.ServerVersion}}")
    Write-Host "Docker server: $dockerVersion"

    if ($buildImageLocally) {
        Write-Step "Build production Web image"
        $localImageSource = "https://github.com/tov-vl/gold-src-ops"
        $localImageRevision = Invoke-ExternalCapture -FilePath "git" -Arguments @(
            "rev-parse",
            "HEAD")
        $localImageVersion = "smoke-$runId"

        Invoke-External -FilePath "docker" -Arguments @(
            "build",
            "--progress=plain",
            "--file",
            (Join-Path $repoRoot "Dockerfile.web"),
            "--label",
            "org.opencontainers.image.source=$localImageSource",
            "--label",
            "org.opencontainers.image.revision=$localImageRevision",
            "--label",
            "org.opencontainers.image.version=$localImageVersion",
            "--label",
            "org.opencontainers.image.licenses=MIT",
            "--tag",
            $imageTag,
            $repoRoot)
        $imageBuilt = $true
        $ExpectedImageSource = $localImageSource
        $ExpectedImageRevision = $localImageRevision
        $ExpectedImageVersion = $localImageVersion
    }
    else {
        Write-Step "Pull production Web image by digest"
        Invoke-External -FilePath "docker" -Arguments @("pull", $imageTag)

        $repoDigests = @(
            Invoke-ExternalCapture -FilePath "docker" -Arguments @(
                "image",
                "inspect",
                "--format",
                "{{json .RepoDigests}}",
                $imageTag) |
                ConvertFrom-Json)
        $expectedDigest = ($ImageReference -split "@", 2)[1]
        if ($null -eq ($repoDigests | Where-Object {
                    $_.EndsWith("@$expectedDigest", [StringComparison]::Ordinal)
                })) {
            throw "Docker did not retain the requested Web image digest after pull."
        }
    }

    Write-Step "Verify Web runtime image"
    $runtimeUser = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "image",
        "inspect",
        "--format",
        "{{.Config.User}}",
        $imageTag)
    if ($runtimeUser -ne "1654") {
        throw "Web runtime image must configure Unix UID 1654."
    }

    $labels = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "image",
        "inspect",
        "--format",
        "{{json .Config.Labels}}",
        $imageTag) | ConvertFrom-Json -AsHashtable
    $expectedLabels = @{
        "org.opencontainers.image.source" = $ExpectedImageSource
        "org.opencontainers.image.revision" = $ExpectedImageRevision
        "org.opencontainers.image.version" = $ExpectedImageVersion
        "org.opencontainers.image.licenses" = "MIT"
    }
    foreach ($entry in $expectedLabels.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$labels[$entry.Key]) -or
            (-not [string]::IsNullOrWhiteSpace([string]$entry.Value) -and
             [string]$labels[$entry.Key] -cne [string]$entry.Value)) {
            throw "OCI label '$($entry.Key)' does not match the Web image contract."
        }
    }

    if ([string]$labels["org.opencontainers.image.revision"] -notmatch '\A[0-9a-f]{40}\z') {
        throw "OCI revision label must contain a full Git commit SHA."
    }

    $healthCheck = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "image",
        "inspect",
        "--format",
        "{{json .Config.Healthcheck.Test}}",
        $imageTag)
    if (-not $healthCheck.Contains("/health/live", [StringComparison]::Ordinal)) {
        throw "Web image health check must target /health/live."
    }

    $runtimeCheck = 'test "$(id -u)" -ne 0 && ' +
        'test ! -d /usr/share/dotnet/sdk && ' +
        'test -f /app/GoldSrcOps.Web.dll && ' +
        'test -f /app/appsettings.json && ' +
        'test ! -e /app/appsettings.Development.json && ' +
        'test ! -e /app/appsettings.Local.json && ' +
        'test ! -e /app/.env && ' +
        'test ! -e /app/.git && ' +
        'test ! -e /.git'
    Invoke-External -FilePath "docker" -Arguments @(
        "run",
        "--rm",
        "--read-only",
        "--network",
        "none",
        "--entrypoint",
        "/bin/sh",
        $imageTag,
        "-c",
        $runtimeCheck)
    Write-Host "Web runtime user and OCI metadata are valid; SDK, source metadata, and Development settings are absent."

    Write-Step "Start hardened Web container"
    Invoke-External -FilePath "docker" -Arguments @(
        "run",
        "--rm",
        "--detach",
        "--name",
        $containerName,
        "--publish",
        "127.0.0.1::8080",
        "--read-only",
        "--tmpfs",
        "/tmp:rw,noexec,nosuid,size=16m",
        "--cap-drop",
        "ALL",
        "--security-opt",
        "no-new-privileges",
        "--env",
        "ASPNETCORE_ENVIRONMENT=Production",
        "--env",
        "AllowedHosts=127.0.0.1;localhost",
        "--env",
        "GoldSrcOpsApi__BaseUrl=http://127.0.0.1:9/",
        $imageTag)

    $binding = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "port",
        $containerName,
        "8080/tcp")
    if ($binding -notmatch '\A127\.0\.0\.1:(?<port>[0-9]+)\z') {
        throw "Web smoke port must bind only to IPv4 loopback."
    }

    $hostPort = [int]$Matches["port"]
    $healthUri = [Uri]"http://127.0.0.1:$hostPort/health/live"
    $homeUri = [Uri]"http://127.0.0.1:$hostPort/"

    $null = Wait-HttpStatus -Uri $healthUri -TimeoutSeconds $StartupTimeoutSeconds
    Wait-ContainerHealthy -TimeoutSeconds $StartupTimeoutSeconds
    $homeResponse = Wait-HttpStatus -Uri $homeUri -TimeoutSeconds $StartupTimeoutSeconds
    if (-not $homeResponse.Content.Contains("GoldSrcOps", [StringComparison]::Ordinal) -or
        -not $homeResponse.Content.Contains("Status unavailable", [StringComparison]::Ordinal)) {
        throw "Web dashboard did not render its safe API-unavailable state."
    }
    if ($homeResponse.Content.Contains("Loading current status", [StringComparison]::Ordinal) -or
        $homeResponse.Content.Contains("_framework/blazor.web.js", [StringComparison]::Ordinal)) {
        throw "Web dashboard did not complete as a self-contained static SSR response."
    }

    $containerContract = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "inspect",
        "--format",
        "{{.HostConfig.ReadonlyRootfs}}|{{json .HostConfig.CapDrop}}|{{json .HostConfig.SecurityOpt}}",
        $containerName)
    if ($containerContract -notmatch '^true\|.*ALL.*\|.*no-new-privileges') {
        throw "Running Web container does not retain the hardened runtime contract."
    }

    $succeeded = $true
    Write-Step "Web container smoke test passed"
}
finally {
    if (-not $succeeded) {
        & docker container inspect $containerName *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host ""
            Write-Host "Web container logs:"
            & docker logs --tail 100 $containerName 2>&1 |
                ForEach-Object { Write-Host $_ }
        }
    }

    Remove-ContainerIfPresent

    if ($imageBuilt -and -not $KeepImage) {
        Remove-ImageIfPresent
    }
    elseif ($imageBuilt) {
        Write-Host "Kept smoke-test image: $imageTag"
    }

    Pop-Location
}
