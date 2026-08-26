#Requires -Version 7.0

<#
.SYNOPSIS
Builds and smoke-tests the production GoldSrcOps container image.

.DESCRIPTION
Creates isolated Docker resources, verifies the runtime image and fail-fast
configuration behavior, applies EF Core migrations as a separate action, and
checks alert-delivery startup, log safety, API liveness, and PostgreSQL-backed
readiness. Temporary containers, network, and image tag are removed even when
the test fails.

.PARAMETER StartupTimeoutSeconds
How long to wait for PostgreSQL and the API health endpoints.

.PARAMETER KeepImage
Keeps the uniquely tagged API image after cleanup for troubleshooting.
#>

[CmdletBinding()]
param(
    [ValidateRange(10, 300)]
    [int]$StartupTimeoutSeconds = 60,

    [switch]$KeepImage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$apiProject = Join-Path $repoRoot "src/GoldSrcOps.Api"
$apiProjectFile = Join-Path $apiProject "GoldSrcOps.Api.csproj"
$infrastructureProject = Join-Path $repoRoot "src/GoldSrcOps.Infrastructure"
$localDotnetDirectory = Join-Path $repoRoot ".dotnet"
$localDotnetWindows = Join-Path $localDotnetDirectory "dotnet.exe"
$localDotnetUnix = Join-Path $localDotnetDirectory "dotnet"
$dotnet = if (Test-Path -LiteralPath $localDotnetWindows) {
    $localDotnetWindows
}
elseif (Test-Path -LiteralPath $localDotnetUnix) {
    $localDotnetUnix
}
else {
    "dotnet"
}

$runId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$imageTag = "goldsrcops:smoke-$runId"
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
$imageBuilt = $false
$succeeded = $false

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

function Invoke-WithEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Variables,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    $previousValues = @{}

    foreach ($entry in $Variables.GetEnumerator()) {
        $previousValues[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    try {
        & $Action
    }
    finally {
        foreach ($entry in $previousValues.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
        }
    }
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

    Write-Step "Build production image"
    Invoke-External -FilePath "docker" -Arguments @(
        "build",
        "--progress=plain",
        "--tag",
        $imageTag,
        $repoRoot)
    $imageBuilt = $true

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

    $runtimeCheck = 'test "$(id -u)" -ne 0 && ' +
        'test ! -d /usr/share/dotnet/sdk && ' +
        'test -f /app/appsettings.json && ' +
        'test ! -e /app/appsettings.Development.json && ' +
        'test ! -e /app/appsettings.Local.json'
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
    Write-Host "Runtime user: $runtimeUser; SDK and local configuration are absent."

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
        "--publish",
        "127.0.0.1::5432",
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

    $postgresHostPort = Get-PublishedPort -ContainerName $postgresContainer -ContainerPort 5432
    $migrationConnectionString =
        "Host=127.0.0.1;Port=$postgresHostPort;Database=$databaseName;" +
        "Username=$databaseUser;Password=$databasePassword;SSL Mode=Disable;GSS Encryption Mode=Disable"

    Write-Step "Apply EF Core migrations separately"
    Invoke-External -FilePath $dotnet -Arguments @("restore", $apiProjectFile)
    Invoke-External -FilePath $dotnet -Arguments @("tool", "restore")
    Invoke-WithEnvironment -Variables @{
        "ASPNETCORE_ENVIRONMENT" = "Production"
        "Authentication__Schemes__Bearer__ValidAudiences__0" = $testIssuer
        "Authentication__Schemes__Bearer__ValidIssuer" = $testIssuer
        "CommandDispatcher__Enabled" = "false"
        "ConnectionStrings__GoldSrcOps" = $migrationConnectionString
        "Polling__Enabled" = "false"
        "SnapshotRetention__Enabled" = "false"
    } -Action {
        Invoke-External -FilePath $dotnet -Arguments @(
            "tool",
            "run",
            "dotnet-ef",
            "--",
            "database",
            "update",
            "--project",
            $infrastructureProject,
            "--startup-project",
            $apiProject,
            "--",
            "--environment",
            "Production")
    }

    Write-Step "Start hardened API container"
    $containerConnectionString =
        "Host=$postgresContainer;Port=5432;Database=$databaseName;" +
        "Username=$databaseUser;Password=$databasePassword;SSL Mode=Disable;GSS Encryption Mode=Disable"
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

    if ($imageBuilt -and -not $KeepImage) {
        Remove-ImageIfPresent -Tag $imageTag
    }
    elseif ($imageBuilt) {
        Write-Host "Kept smoke-test image: $imageTag"
    }

    Pop-Location
}
