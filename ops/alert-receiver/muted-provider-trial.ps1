#Requires -Version 7.0

<#
.SYNOPSIS
Runs one synthetic AlertReceiver delivery pair against the reviewed muted route.

.DESCRIPTION
Builds the current receiver image, creates an isolated PostgreSQL database,
applies the image-local migrations, and starts exactly one Live provider worker.
It sends one synthetic unavailable event and its exact duplicate, then one
matching recovery and its exact duplicate. Success requires two processed
provider-outbox rows in Trigger/Resolve order with one attempt each.

The provider endpoint and Bearer credential are read only from owner-controlled
files outside the repository. Their values are never printed or retained. The
script does not access the production database or alert outbox. Provider-side
group, resolution, and zero-notification observations remain a separate UI
check after this transport proof.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProviderEndpointFile,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProviderAuthorizationFile,

    [string]$EvidenceFile,

    [ValidateRange(30, 300)]
    [int]$StartupTimeoutSeconds = 120,

    [switch]$Execute,

    [switch]$KeepImage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$imageTag = "goldsrcops-alert-receiver:muted-trial-$runId"
$postgresImage = "postgres:16-alpine"
$postgresContainer = "goldsrcops-receiver-muted-postgres-$runId"
$receiverContainer = "goldsrcops-receiver-muted-runtime-$runId"
$dataVolume = "goldsrcops-receiver-muted-data-$runId"
$socketVolume = "goldsrcops-receiver-muted-socket-$runId"
$databaseName = "goldsrcops_receiver"
$databaseUser = "goldsrcops_receiver"
$databasePassword = "goldsrcops-receiver-muted-$runId"
$receiverAuthorization = "Bearer receiver-muted-$runId"
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-receiver-muted-$runId"
$databaseConnectionFile = Join-Path $temporaryDirectory "database-connection"
$receiverAuthorizationFile = Join-Path $temporaryDirectory "receiver-authorization"
$postgresPasswordFile = Join-Path $temporaryDirectory "postgres-password"
$stagedProviderEndpointFile = Join-Path $temporaryDirectory "provider-endpoint"
$stagedProviderAuthorizationFile = Join-Path $temporaryDirectory "provider-authorization"
$imageBuilt = $false
$postgresCreated = $false
$dataVolumeCreated = $false
$socketVolumeCreated = $false
$succeeded = $false

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Name)

    Write-Host ""
    Write-Host "==> $Name"
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-ExternalCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = @(& $FilePath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $text = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "Command '$FilePath' failed with exit code $exitCode. $text"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $text
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

function Read-ValidatedProviderSecret {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (Test-PathInsideRepository -Path $resolved) {
        throw "$Name must live outside the repository."
    }

    $value = [IO.File]::ReadAllText($resolved)
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match '[\r\n]') {
        throw "$Name must contain exactly one non-empty line."
    }

    return [pscustomobject]@{
        Path = $resolved
        Value = $value
    }
}

function Wait-Postgres {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $result = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "exec", $postgresContainer,
            "/bin/sh", "-ec",
            'export PGPASSWORD="$(cat /run/secrets/postgres-password)"; exec pg_isready --host=/var/run/postgresql --username="$POSTGRES_USER" --dbname="$POSTGRES_DB"') -AllowFailure
        if ($result.ExitCode -eq 0) {
            return
        }

        Start-Sleep -Seconds 1
    }

    throw "Muted-provider PostgreSQL did not become ready in time."
}

function Invoke-ReceiverSql {
    param([Parameter(Mandatory = $true)][string]$Sql)

    $result = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "exec", "--env", "TRIAL_SQL=$Sql", $postgresContainer,
        "/bin/sh", "-ec",
        'export PGPASSWORD="$(cat /run/secrets/postgres-password)"; exec psql --host=/var/run/postgresql --username="$POSTGRES_USER" --dbname="$POSTGRES_DB" --no-align --tuples-only --set ON_ERROR_STOP=1 --command "$TRIAL_SQL"')
    return $result.Output.Trim()
}

function Wait-ReceiverHealth {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $probe = @'
exec 3<>/dev/tcp/127.0.0.1/8080
printf 'GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3
read -r status <&3
[[ "$status" == *" 200 "* ]]
'@
        $result = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "exec", $receiverContainer, "bash", "-c", $probe) -AllowFailure
        if ($result.ExitCode -eq 0) {
            return
        }

        $state = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
            "inspect", "--format", "{{.State.Status}}", $receiverContainer) -AllowFailure
        if ($state.ExitCode -eq 0 -and $state.Output -in @("dead", "exited")) {
            throw "AlertReceiver stopped before readiness passed."
        }

        Start-Sleep -Seconds 1
    }

    throw "AlertReceiver readiness did not pass in time."
}

function Invoke-AvailabilityEvent {
    param(
        [Parameter(Mandatory = $true)][string]$Body,
        [Parameter(Mandatory = $true)][Guid]$EventId
    )

    $request = @'
set -eu
authorization="$(cat /run/secrets/receiver-authorization)"
content_length="${#TRIAL_BODY}"
exec 3<>/dev/tcp/127.0.0.1/8080
printf 'POST /api/v1/availability-events HTTP/1.1\r\nHost: localhost\r\nAuthorization: %s\r\nIdempotency-Key: %s\r\nContent-Type: application/json\r\nContent-Length: %s\r\nConnection: close\r\n\r\n%s' "$authorization" "$TRIAL_EVENT_ID" "$content_length" "$TRIAL_BODY" >&3
IFS= read -r status <&3
printf '%s' "$status"
'@
    $result = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
        "exec",
        "--env", "TRIAL_BODY=$Body",
        "--env", "TRIAL_EVENT_ID=$($EventId.ToString('D'))",
        $receiverContainer,
        "bash", "-c", $request)
    return $result.Output.Trim()
}

function Wait-ProcessedEvent {
    param([Parameter(Mandatory = $true)][Guid]$EventId)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $state = Invoke-ReceiverSql -Sql @"
SELECT "Status" || ':' || "AttemptCount"::text
FROM receiver.provider_outbox_messages
WHERE "SourceEventId" = '$($EventId.ToString('D'))';
"@
        if ($state -eq "Processed:1") {
            return
        }
        if ($state -match '^DeadLetter:') {
            throw "Provider delivery reached a terminal dead-letter state."
        }

        Start-Sleep -Seconds 1
    }

    throw "Provider delivery did not complete in the bounded trial window."
}

function Remove-DockerResource {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("container", "volume")][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $arguments = if ($Kind -eq "container") {
        @("rm", "--force", $Name)
    }
    else {
        @("volume", "rm", "--force", $Name)
    }
    [void](Invoke-ExternalCapture -FilePath "docker" -Arguments $arguments -AllowFailure)
}

if (-not $Execute) {
    Write-Host "PLAN: validate two owner-controlled provider secret files outside Git"
    Write-Host "PLAN: build the current AlertReceiver image and create only disposable local PostgreSQL resources"
    Write-Host "PLAN: send one synthetic unavailable/recovered pair plus exact duplicates through one Live provider worker"
    Write-Host "PLAN: require two one-attempt processed actions in Trigger/Resolve order, then remove every local trial resource"
    Write-Host "PLAN_ONLY: no secret was read and no container, database, provider request, or evidence file was created; add -Execute to run."
    return
}

$endpointSecret = Read-ValidatedProviderSecret -Path $ProviderEndpointFile -Name "Provider endpoint"
$authorizationSecret = Read-ValidatedProviderSecret `
    -Path $ProviderAuthorizationFile `
    -Name "Provider authorization"
$endpointUri = $null
if (-not [Uri]::TryCreate($endpointSecret.Value, [UriKind]::Absolute, [ref]$endpointUri) -or
    $endpointUri.Scheme -ne [Uri]::UriSchemeHttps -or
    -not [string]::IsNullOrEmpty($endpointUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($endpointUri.Fragment)) {
    throw "Provider endpoint must be an absolute HTTPS URI without user info or fragment."
}
if ($authorizationSecret.Value.Length -gt 8192 -or
    $authorizationSecret.Value -notmatch '\ABearer [^\s]+\z') {
    throw "Provider authorization must be a bounded single-line Bearer credential."
}
if (-not [string]::IsNullOrWhiteSpace($EvidenceFile) -and
    (Test-PathInsideRepository -Path ([IO.Path]::GetFullPath($EvidenceFile)))) {
    throw "Muted-provider trial evidence must stay outside the repository."
}

$incidentId = [Guid]::NewGuid()
$openingEventId = [Guid]::NewGuid()
$recoveryEventId = [Guid]::NewGuid()
$serverId = [Guid]::NewGuid()
$closedAtUtc = [DateTimeOffset]::FromUnixTimeSeconds(
    [DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
$openedAtUtc = $closedAtUtc.AddMinutes(-3)

Push-Location $repoRoot
try {
    Write-Step "Build the reviewed AlertReceiver image"
    Invoke-External -FilePath "docker" -Arguments @(
        "build", "--file", "Dockerfile.receiver", "--tag", $imageTag, ".")
    $imageBuilt = $true

    Write-Step "Create isolated receiver PostgreSQL and apply migrations"
    New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
    [IO.File]::WriteAllText($postgresPasswordFile, $databasePassword)
    [IO.File]::WriteAllText(
        $databaseConnectionFile,
        "Host=/var/run/postgresql;Port=5432;Database=$databaseName;Username=$databaseUser;Password=$databasePassword;SSL Mode=Disable;Timeout=5;Command Timeout=30")
    [IO.File]::WriteAllText($receiverAuthorizationFile, $receiverAuthorization)
    [IO.File]::WriteAllText(
        $stagedProviderEndpointFile,
        $endpointSecret.Value,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $stagedProviderAuthorizationFile,
        $authorizationSecret.Value,
        [Text.UTF8Encoding]::new($false))

    Invoke-External -FilePath "docker" -Arguments @("volume", "create", $dataVolume)
    $dataVolumeCreated = $true
    Invoke-External -FilePath "docker" -Arguments @("volume", "create", $socketVolume)
    $socketVolumeCreated = $true
    Invoke-External -FilePath "docker" -Arguments @(
        "run", "--detach", "--name", $postgresContainer,
        "--network", "none",
        "--env", "PGDATA=/var/lib/postgresql/data/pgdata",
        "--env", "POSTGRES_DB=$databaseName",
        "--env", "POSTGRES_USER=$databaseUser",
        "--env", "POSTGRES_PASSWORD_FILE=/run/secrets/postgres-password",
        "--env", "POSTGRES_INITDB_ARGS=--auth-local=scram-sha-256 --auth-host=scram-sha-256",
        "--mount", "type=bind,source=$postgresPasswordFile,target=/run/secrets/postgres-password,readonly",
        "--mount", "type=volume,source=$dataVolume,target=/var/lib/postgresql/data",
        "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
        $postgresImage)
    $postgresCreated = $true
    Wait-Postgres
    Invoke-External -FilePath "docker" -Arguments @(
        "run", "--rm", "--network", "none", "--read-only",
        "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m,mode=0700,uid=1654,gid=1654",
        "--mount", "type=bind,source=$databaseConnectionFile,target=/run/secrets/receiver-database-connection,readonly",
        "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
        "--env", "DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net",
        $imageTag, "migrate", "--no-color", "--prefix-output")

    Write-Step "Start one Live worker against the reviewed muted route"
    Invoke-External -FilePath "docker" -Arguments @(
        "run", "--detach", "--name", $receiverContainer,
        "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
        "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
        "--mount", "type=bind,source=$databaseConnectionFile,target=/run/secrets/receiver-database-connection,readonly",
        "--mount", "type=bind,source=$receiverAuthorizationFile,target=/run/secrets/receiver-authorization,readonly",
        "--mount", "type=bind,source=$stagedProviderEndpointFile,target=/run/secrets/provider-endpoint,readonly",
        "--mount", "type=bind,source=$stagedProviderAuthorizationFile,target=/run/secrets/provider-authorization,readonly",
        "--mount", "type=volume,source=$socketVolume,target=/var/run/postgresql",
        "--env", "ASPNETCORE_ENVIRONMENT=Production",
        "--env", "AllowedHosts=localhost",
        "--env", "Receiver__Mode=Live",
        "--env", "ProviderDelivery__Enabled=true",
        "--env", "ProviderDelivery__MaxConcurrency=1",
        "--env", "ProviderDelivery__LoopDelay=00:00:00.250",
        $imageTag)
    Wait-ReceiverHealth

    $openingBody = [ordered]@{
        payloadVersion = 1
        eventId = $openingEventId
        incidentId = $incidentId
        serverId = $serverId
        eventType = "server.availability.unavailable"
        serverName = "GoldSrcOps muted compatibility trial"
        occurredAtUtc = $openedAtUtc.ToString("O")
        openedAtUtc = $openedAtUtc.ToString("O")
        closedAtUtc = $null
        reason = "Synthetic bounded provider compatibility trial."
        consecutiveFailures = 3
        durationSeconds = $null
    } | ConvertTo-Json -Compress
    $recoveryBody = [ordered]@{
        payloadVersion = 1
        eventId = $recoveryEventId
        incidentId = $incidentId
        serverId = $serverId
        eventType = "server.availability.recovered"
        serverName = "GoldSrcOps muted compatibility trial"
        occurredAtUtc = $closedAtUtc.ToString("O")
        openedAtUtc = $openedAtUtc.ToString("O")
        closedAtUtc = $closedAtUtc.ToString("O")
        reason = "Synthetic bounded provider recovery."
        consecutiveFailures = 3
        durationSeconds = [long][Math]::Floor(($closedAtUtc - $openedAtUtc).TotalSeconds)
    } | ConvertTo-Json -Compress

    Write-Step "Deliver one opening event and its exact duplicate"
    $openingStatus = Invoke-AvailabilityEvent -Body $openingBody -EventId $openingEventId
    $openingDuplicateStatus = Invoke-AvailabilityEvent -Body $openingBody -EventId $openingEventId
    if ($openingStatus -notmatch '^HTTP/1\.1 202 ' -or
        $openingDuplicateStatus -notmatch '^HTTP/1\.1 204 ') {
        throw "Opening event did not preserve the 202/204 idempotency contract: '$openingStatus' / '$openingDuplicateStatus'."
    }
    Wait-ProcessedEvent -EventId $openingEventId

    Write-Step "Deliver one recovery event and its exact duplicate"
    $recoveryStatus = Invoke-AvailabilityEvent -Body $recoveryBody -EventId $recoveryEventId
    $recoveryDuplicateStatus = Invoke-AvailabilityEvent -Body $recoveryBody -EventId $recoveryEventId
    if ($recoveryStatus -notmatch '^HTTP/1\.1 202 ' -or
        $recoveryDuplicateStatus -notmatch '^HTTP/1\.1 204 ') {
        throw "Recovery event did not preserve the 202/204 idempotency contract: '$recoveryStatus' / '$recoveryDuplicateStatus'."
    }
    Wait-ProcessedEvent -EventId $recoveryEventId

    $actionSummary = Invoke-ReceiverSql -Sql @"
SELECT string_agg("Action" || ':' || "Status" || ':' || "AttemptCount"::text, ',' ORDER BY "CreatedAtUtc", "Id")
FROM receiver.provider_outbox_messages
WHERE "IncidentId" = '$($incidentId.ToString('D'))';
"@
    if ($actionSummary -ne "Trigger:Processed:1,Resolve:Processed:1") {
        throw "Provider outbox did not preserve one-attempt Trigger/Resolve ordering."
    }
    $eventCount = [int](Invoke-ReceiverSql -Sql @"
SELECT COUNT(*) FROM receiver.events WHERE "IncidentId" = '$($incidentId.ToString('D'))';
"@)
    if ($eventCount -ne 2) {
        throw "Muted-provider trial persisted $eventCount source events instead of 2."
    }

    if (-not [string]::IsNullOrWhiteSpace($EvidenceFile)) {
        $evidencePath = [IO.Path]::GetFullPath($EvidenceFile)
        $evidenceDirectory = Split-Path -Parent $evidencePath
        if (-not [string]::IsNullOrEmpty($evidenceDirectory)) {
            New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
        }
        [ordered]@{
            Action = "AlertReceiverMutedProviderTrial"
            CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            IncidentId = $incidentId
            OpeningEventId = $openingEventId
            OpeningStatus = 202
            OpeningDuplicateStatus = 204
            RecoveryEventId = $recoveryEventId
            RecoveryStatus = 202
            RecoveryDuplicateStatus = 204
            ProviderActions = @("Trigger:Processed:1", "Resolve:Processed:1")
            ProductionQueueTouched = $false
            ProviderRoute = "DedicatedMutedRoute"
            ProviderObservationRequired = $true
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
        if (-not $IsWindows) {
            [IO.File]::SetUnixFileMode(
                $evidencePath,
                [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        }
    }

    $succeeded = $true
    Write-Step "Muted-provider transport proof passed"
    Write-Host "Synthetic incident: $($incidentId.ToString('D'))"
    Write-Host "Provider actions: Trigger:Processed:1, Resolve:Processed:1"
    Write-Host "NEXT_GATE: confirm one resolved provider group and zero involved users or notification effects."
}
finally {
    if (-not $succeeded) {
        foreach ($container in @($receiverContainer, $postgresContainer)) {
            $exists = Invoke-ExternalCapture -FilePath "docker" -Arguments @(
                "container", "inspect", $container) -AllowFailure
            if ($exists.ExitCode -eq 0) {
                Write-Host ""
                Write-Host "Sanitized local logs for $container`:"
                & docker logs --tail 100 $container 2>&1 |
                    ForEach-Object {
                        [string]$_ -replace [regex]::Escape($endpointSecret.Value), "[provider-endpoint]" `
                            -replace [regex]::Escape($authorizationSecret.Value), "[provider-authorization]"
                    } |
                    ForEach-Object { Write-Host $_ }
            }
        }
    }

    Remove-DockerResource -Kind container -Name $receiverContainer
    if ($postgresCreated) {
        Remove-DockerResource -Kind container -Name $postgresContainer
    }
    if ($dataVolumeCreated) {
        Remove-DockerResource -Kind volume -Name $dataVolume
    }
    if ($socketVolumeCreated) {
        Remove-DockerResource -Kind volume -Name $socketVolume
    }

    if (Test-Path -LiteralPath $temporaryDirectory) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
        if (-not $resolvedTemporaryDirectory.StartsWith(
                $temporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a muted-provider trial directory outside the temporary root."
        }
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
    }

    if ($imageBuilt -and -not $KeepImage) {
        [void](Invoke-ExternalCapture -FilePath "docker" -Arguments @(
                "image", "rm", "--force", $imageTag) -AllowFailure)
    }
    elseif ($imageBuilt) {
        Write-Host "Kept AlertReceiver trial image: $imageTag"
    }

    Pop-Location
}
