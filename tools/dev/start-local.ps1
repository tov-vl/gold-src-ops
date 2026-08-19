#Requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$SkipDocker,
    [switch]$SkipRestore,
    [switch]$SkipToolRestore,
    [switch]$SkipMigrations,
    [switch]$NoRun,
    [int]$PostgresTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$solution = Join-Path $repoRoot "GoldSrcOps.sln"
$composeFile = Join-Path $repoRoot "ops\docker-compose.yml"
$apiProject = Join-Path $repoRoot "src\GoldSrcOps.Api"
$infrastructureProject = Join-Path $repoRoot "src\GoldSrcOps.Infrastructure"
$localDotnet = Join-Path $repoRoot ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { "dotnet" }

function Write-Step {
    param([string]$Name)

    Write-Host ""
    Write-Host "==> $Name"
}

function Invoke-External {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Wait-PostgresContainer {
    param(
        [string]$ContainerName,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $status = (& docker inspect --format "{{.State.Health.Status}}" $ContainerName 2>$null) -join ""

        if ($LASTEXITCODE -eq 0 -and $status -eq "healthy") {
            Write-Host "PostgreSQL container is healthy."
            return
        }

        if ([string]::IsNullOrWhiteSpace($status)) {
            $status = "not found"
        }

        Write-Host "Waiting for PostgreSQL healthcheck: $status"
        Start-Sleep -Seconds 2
    }

    throw "PostgreSQL container '$ContainerName' did not become healthy within $TimeoutSeconds seconds."
}

Push-Location -LiteralPath $repoRoot
try {
    if (-not $SkipDocker) {
        Write-Step "Start PostgreSQL"
        Invoke-External -FilePath "docker" -Arguments @("compose", "-f", $composeFile, "up", "-d", "postgres")
        Wait-PostgresContainer -ContainerName "goldsrcops-postgres" -TimeoutSeconds $PostgresTimeoutSeconds
    }
    else {
        Write-Host "Skipping Docker startup."
    }

    if (-not $SkipRestore) {
        Write-Step "Restore solution packages"
        Invoke-External -FilePath $dotnet -Arguments @("restore", $solution)
    }
    else {
        Write-Host "Skipping solution restore."
    }

    if (-not $SkipToolRestore) {
        Write-Step "Restore .NET local tools"
        Invoke-External -FilePath $dotnet -Arguments @("tool", "restore")
    }
    else {
        Write-Host "Skipping .NET tool restore."
    }

    if (-not $SkipMigrations) {
        Write-Step "Apply EF Core migrations"
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
            "Development")
    }
    else {
        Write-Host "Skipping database migrations."
    }

    if ($NoRun) {
        Write-Step "Ready"
        Write-Host "Skipped API launch because -NoRun was specified."
        Write-Host "Start the API manually with:"
        Write-Host "  $dotnet run --project $apiProject --launch-profile http"
        return
    }

    Write-Step "Run API"
    Write-Host "API URL: http://localhost:5142"
    Write-Host "Health:  http://localhost:5142/health/ready"
    Write-Host "Metrics: http://localhost:5142/metrics"
    Invoke-External -FilePath $dotnet -Arguments @("run", "--project", $apiProject, "--launch-profile", "http")
}
finally {
    Pop-Location
}
