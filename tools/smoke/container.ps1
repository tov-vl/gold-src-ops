#Requires -Version 7.0

<#
.SYNOPSIS
Builds and smoke-tests the production GoldSrcOps container image.

.DESCRIPTION
Creates isolated Docker resources, verifies the runtime image and fail-fast
configuration behavior, applies the image-contained EF Core migration bundle
twice through its production entrypoint, and checks alert-delivery startup, log
safety, API liveness, and PostgreSQL-backed readiness. Temporary containers,
network, secret file, and image tag are removed even when the test fails.

.PARAMETER StartupTimeoutSeconds
How long to wait for PostgreSQL and the API health endpoints.

.PARAMETER KeepImage
Keeps the uniquely tagged API image after cleanup for troubleshooting.

.PARAMETER ImageReference
Pulls and tests an existing production image instead of building one. The
reference must use an immutable sha256 digest.

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
    [int]$StartupTimeoutSeconds = 60,

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
$imageTag = if ($buildImageLocally) { "goldsrcops:smoke-$runId" } else { $ImageReference }
$networkName = "goldsrcops-smoke-$runId"
$postgresContainer = "goldsrcops-smoke-postgres-$runId"
$apiContainer = "goldsrcops-smoke-api-$runId"
$failFastContainer = "goldsrcops-smoke-failfast-$runId"
$alertFailFastContainer = "goldsrcops-smoke-alert-failfast-$runId"
$databaseName = "goldsrcops"
$databaseUser = "goldsrcops"
$databasePassword = "goldsrcops-smoke"
$testIssuer = "goldsrcops-container-smoke"
$alertWebhookUrl = "https://alerts.example.invalid/goldsrcops"
$alertAuthorizationMarker = "Bearer goldsrcops-smoke-secret-$runId"
$migrationSecretFile = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-smoke-$runId-database-connection"
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

    $output = @(& $FilePath @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
    }

    return (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
}

function Wait-ContainerHealthy {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $state = (& docker inspect `
            --format "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}" `
            $ContainerName 2>$null) -join ""

        if ($LASTEXITCODE -eq 0) {
            if ($state -eq "running|healthy") {
                return
            }

            if ($state.StartsWith("exited|", [StringComparison]::Ordinal) -or
                $state.StartsWith("dead|", [StringComparison]::Ordinal)) {
                throw "Container '$ContainerName' stopped before becoming healthy."
            }
        }

        Start-Sleep -Seconds 1
    }

    throw "Container '$ContainerName' did not become healthy within $TimeoutSeconds seconds."
}

function Get-PublishedPort {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [int]$ContainerPort
    )

    $mapping = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "port",
        $ContainerName,
        "$ContainerPort/tcp")

    if ($mapping -notmatch ":(?<port>[0-9]+)\s*$") {
        throw "Docker did not publish container port $ContainerPort for '$ContainerName'."
    }

    return [int]$Matches["port"]
}

function Wait-HttpHealth {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [Uri]$Uri,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = "No response received."

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $running = (& docker inspect --format "{{.State.Running}}" $ContainerName 2>$null) -join ""
        if ($LASTEXITCODE -ne 0 -or $running -ne "true") {
            throw "API container '$ContainerName' stopped before $($Uri.AbsolutePath) became healthy."
        }

        try {
            $response = Invoke-WebRequest `
                -UseBasicParsing `
                -Uri $Uri `
                -MaximumRedirection 0 `
                -TimeoutSec 5

            if ($response.StatusCode -eq 200 -and $response.Content.Trim() -eq "Healthy") {
                return
            }

            $lastError = "HTTP $($response.StatusCode) with body '$($response.Content.Trim())'."
        }
        catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Seconds 1
    }

    throw "Health endpoint '$Uri' did not become healthy within $TimeoutSeconds seconds. Last error: $lastError"
}

function Invoke-MigrationBundle {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Image,

        [Parameter(Mandatory = $true)]
        [string]$Network,

        [Parameter(Mandatory = $true)]
        [string]$ConnectionFile
    )

    Invoke-External -FilePath "docker" -Arguments @(
        "run",
        "--rm",
        "--network",
        $Network,
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
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net",
        "--mount",
        "type=bind,source=$ConnectionFile,target=/run/secrets/database-connection,readonly",
        "--entrypoint",
        "/bin/sh",
        $Image,
        "/app/api-entrypoint.sh",
        "migrate",
        "--no-color",
        "--prefix-output")
}

function Remove-ContainerIfPresent {
    param([string]$ContainerName)

    & docker container inspect $ContainerName *> $null
    if ($LASTEXITCODE -ne 0) {
        return
    }

    & docker container rm --force $ContainerName *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not remove container '$ContainerName'."
    }
}

function Remove-NetworkIfPresent {
    param([string]$Name)

    & docker network inspect $Name *> $null
    if ($LASTEXITCODE -ne 0) {
        return
    }

    & docker network rm $Name *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not remove network '$Name'."
    }
}

function Remove-ImageIfPresent {
    param([string]$Tag)

    & docker image inspect $Tag *> $null
    if ($LASTEXITCODE -ne 0) {
        return
    }

    & docker image rm --force $Tag *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not remove image '$Tag'."
    }
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
        Write-Step "Build production image"
        $localImageSource = "https://github.com/tov-vl/gold-src-ops"
        $localImageRevision = Invoke-ExternalCapture -FilePath "git" -Arguments @(
            "rev-parse",
            "HEAD")
        $localImageVersion = "smoke-$runId"

        Invoke-External -FilePath "docker" -Arguments @(
            "build",
            "--progress=plain",
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
        Write-Step "Pull production image by digest"
        Invoke-External -FilePath "docker" -Arguments @(
            "pull",
            $imageTag)

        $repoDigestsJson = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "image",
            "inspect",
            "--format",
            "{{json .RepoDigests}}",
            $imageTag)
        $repoDigests = @($repoDigestsJson | ConvertFrom-Json)
        $expectedDigest = ($ImageReference -split "@", 2)[1]
        $matchingDigest = $repoDigests | Where-Object {
            $_.EndsWith("@$expectedDigest", [StringComparison]::Ordinal)
        }

        if ($null -eq $matchingDigest) {
            throw "Docker did not retain the requested image digest after pull."
        }
    }

    Write-Step "Verify runtime image"
    $runtimeUser = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "image",
        "inspect",
        "--format",
        "{{.Config.User}}",
        $imageTag)

    if ([string]::IsNullOrWhiteSpace($runtimeUser) -or $runtimeUser -in @("0", "root")) {
        throw "Runtime image must configure a non-root user."
    }

    $labelsJson = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "image",
        "inspect",
        "--format",
        "{{json .Config.Labels}}",
        $imageTag)
    $labels = $labelsJson | ConvertFrom-Json -AsHashtable

    if ($null -eq $labels) {
        throw "Runtime image does not contain OCI labels."
    }

    $requiredLabels = @(
        "org.opencontainers.image.source",
        "org.opencontainers.image.revision",
        "org.opencontainers.image.version",
        "org.opencontainers.image.licenses")

    foreach ($labelName in $requiredLabels) {
        if (-not $labels.ContainsKey($labelName) -or
            [string]::IsNullOrWhiteSpace([string]$labels[$labelName])) {
            throw "Runtime image is missing required OCI label '$labelName'."
        }
    }

    $sourceUri = $null
    if (-not [Uri]::TryCreate(
        [string]$labels["org.opencontainers.image.source"],
        [UriKind]::Absolute,
        [ref]$sourceUri) -or
        $sourceUri.Scheme -ne [Uri]::UriSchemeHttps) {
        throw "OCI source label must be an absolute HTTPS URL."
    }

    if ([string]$labels["org.opencontainers.image.revision"] -notmatch '\A[0-9a-f]{40}\z') {
        throw "OCI revision label must contain a full Git commit SHA."
    }

    if ([string]$labels["org.opencontainers.image.licenses"] -ne "MIT") {
        throw "OCI license label must be 'MIT'."
    }

    $expectedLabels = @{
        "org.opencontainers.image.source" = $ExpectedImageSource
        "org.opencontainers.image.revision" = $ExpectedImageRevision
        "org.opencontainers.image.version" = $ExpectedImageVersion
    }

    foreach ($entry in $expectedLabels.GetEnumerator()) {
        if (-not [string]::IsNullOrWhiteSpace([string]$entry.Value) -and
            [string]$labels[$entry.Key] -cne [string]$entry.Value) {
            throw "OCI label '$($entry.Key)' does not match the expected value."
        }
    }

    $runtimeCheck = 'test "$(id -u)" -ne 0 && ' +
        'test ! -d /usr/share/dotnet/sdk && ' +
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
    Write-Host "Runtime user: $runtimeUser; OCI metadata is valid; SDK, repository metadata, and local configuration are absent."

    Write-Step "Verify missing-configuration fail-fast"
    $failFastOutput = @(& docker run `
        --rm `
        --name $failFastContainer `
        --read-only `
        --network none `
        $imageTag 2>&1)
    $failFastExitCode = $LASTEXITCODE
    $failFastText = ($failFastOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine

    if ($failFastExitCode -eq 0) {
        throw "API container unexpectedly started without its required connection string."
    }

    if (-not $failFastText.Contains(
        "Connection string 'GoldSrcOps' is not configured.",
        [StringComparison]::Ordinal)) {
        throw "API container failed for an unexpected reason when required configuration was omitted."
    }

    Write-Host "Missing connection string produced the expected non-zero exit."

    Write-Step "Verify Production webhook HTTPS validation"
    $alertFailFastOutput = @(& docker run `
        --rm `
        --name $alertFailFastContainer `
        --read-only `
        --network none `
        --env "ASPNETCORE_ENVIRONMENT=Production" `
        --env "ConnectionStrings__GoldSrcOps=Host=localhost;Database=$databaseName;Username=$databaseUser;Password=$databasePassword" `
        --env "Authentication__Schemes__Bearer__ValidIssuer=$testIssuer" `
        --env "Authentication__Schemes__Bearer__ValidAudiences__0=$testIssuer" `
        --env "Polling__Enabled=false" `
        --env "CommandDispatcher__Enabled=false" `
        --env "SnapshotRetention__Enabled=false" `
        --env "AlertDelivery__Enabled=true" `
        --env "AlertDelivery__WebhookUrl=http://alerts.example.invalid/goldsrcops" `
        $imageTag 2>&1)
    $alertFailFastExitCode = $LASTEXITCODE
    $alertFailFastText =
        ($alertFailFastOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine

    if ($alertFailFastExitCode -eq 0) {
        throw "API container unexpectedly accepted an HTTP webhook in Production."
    }

    if (-not $alertFailFastText.Contains(
        "Configuration value 'AlertDelivery:WebhookUrl' must be an absolute HTTPS URL without user information.",
        [StringComparison]::Ordinal)) {
        throw "API container failed for an unexpected reason when an HTTP webhook was configured in Production."
    }

    Write-Host "Production rejected the HTTP webhook with the expected non-zero exit."

    Write-Step "Start isolated PostgreSQL"
    Invoke-External -FilePath "docker" -Arguments @("network", "create", $networkName)
    Invoke-External -FilePath "docker" -Arguments @(
        "run",
        "--rm",
        "--detach",
        "--name",
        $postgresContainer,
        "--network",
        $networkName,
        "--tmpfs",
        "/var/lib/postgresql/data:rw,nosuid,size=256m",
        "--env",
        "POSTGRES_DB=$databaseName",
        "--env",
        "POSTGRES_USER=$databaseUser",
        "--env",
        "POSTGRES_PASSWORD=$databasePassword",
        "--health-cmd",
        "pg_isready -U $databaseUser -d $databaseName",
        "--health-interval",
        "1s",
        "--health-timeout",
        "5s",
        "--health-retries",
        "30",
        "postgres:16-alpine")
    Wait-ContainerHealthy -ContainerName $postgresContainer -TimeoutSeconds $StartupTimeoutSeconds

    $containerConnectionString =
        "Host=$postgresContainer;Port=5432;Database=$databaseName;" +
        "Username=$databaseUser;Password=$databasePassword;SSL Mode=Disable;GSS Encryption Mode=Disable"
    [IO.File]::WriteAllText(
        $migrationSecretFile,
        $containerConnectionString,
        [System.Text.UTF8Encoding]::new($false))

    Write-Step "Apply EF Core migration bundle"
    Invoke-MigrationBundle `
        -Image $imageTag `
        -Network $networkName `
        -ConnectionFile $migrationSecretFile

    Write-Step "Reapply EF Core migration bundle"
    Invoke-MigrationBundle `
        -Image $imageTag `
        -Network $networkName `
        -ConnectionFile $migrationSecretFile

    Write-Step "Start hardened API container"
    Invoke-External -FilePath "docker" -Arguments @(
        "run",
        "--rm",
        "--detach",
        "--name",
        $apiContainer,
        "--network",
        $networkName,
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
        "ConnectionStrings__GoldSrcOps=$containerConnectionString",
        "--env",
        "Authentication__Schemes__Bearer__ValidIssuer=$testIssuer",
        "--env",
        "Authentication__Schemes__Bearer__ValidAudiences__0=$testIssuer",
        "--env",
        "Polling__Enabled=false",
        "--env",
        "CommandDispatcher__Enabled=false",
        "--env",
        "SnapshotRetention__Enabled=false",
        "--env",
        "AlertDelivery__Enabled=true",
        "--env",
        "AlertDelivery__WebhookUrl=$alertWebhookUrl",
        "--env",
        "AlertDelivery__Authorization=$alertAuthorizationMarker",
        $imageTag)

    $apiHostPort = Get-PublishedPort -ContainerName $apiContainer -ContainerPort 8080
    $liveUri = [Uri]"http://127.0.0.1:$apiHostPort/health/live"
    $readyUri = [Uri]"http://127.0.0.1:$apiHostPort/health/ready"

    Write-Step "Verify health endpoints"
    Wait-HttpHealth `
        -ContainerName $apiContainer `
        -Uri $liveUri `
        -TimeoutSeconds $StartupTimeoutSeconds
    Write-Host "Liveness: 200 Healthy"

    Wait-HttpHealth `
        -ContainerName $apiContainer `
        -Uri $readyUri `
        -TimeoutSeconds $StartupTimeoutSeconds
    Write-Host "Readiness: 200 Healthy"

    Write-Step "Verify alert delivery startup and log safety"
    $apiLogs = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "logs",
        $apiContainer)

    if (-not $apiLogs.Contains(
        "Alert delivery service started",
        [StringComparison]::Ordinal)) {
        throw "API container did not start the enabled alert delivery service."
    }

    foreach ($forbiddenValue in @($alertWebhookUrl, $alertAuthorizationMarker)) {
        if ($apiLogs.Contains($forbiddenValue, [StringComparison]::Ordinal)) {
            throw "API container logs exposed alert delivery configuration."
        }
    }

    Write-Host "Alert delivery started without logging its endpoint or authorization value."

    $succeeded = $true
    Write-Step "Container smoke test passed"
}
finally {
    if (-not $succeeded) {
        & docker container inspect $apiContainer *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host ""
            Write-Host "API container logs:"
            & docker logs $apiContainer 2>&1 | ForEach-Object { Write-Host $_ }
        }
    }

    Remove-ContainerIfPresent -ContainerName $apiContainer
    Remove-ContainerIfPresent -ContainerName $failFastContainer
    Remove-ContainerIfPresent -ContainerName $alertFailFastContainer
    Remove-ContainerIfPresent -ContainerName $postgresContainer
    Remove-NetworkIfPresent -Name $networkName

    if (Test-Path -LiteralPath $migrationSecretFile) {
        Remove-Item -LiteralPath $migrationSecretFile -Force
    }

    if ($imageBuilt -and -not $KeepImage) {
        Remove-ImageIfPresent -Tag $imageTag
    }
    elseif ($imageBuilt) {
        Write-Host "Kept smoke-test image: $imageTag"
    }

    Pop-Location
}
