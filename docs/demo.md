# GoldSrcOps v1 Demo

This guide presents the verified v1 behavior in five to ten minutes. It keeps
the full smoke procedure in `docs/smoke-test.md` and focuses on the evidence that
is useful in a portfolio walkthrough.

The commands below assume PowerShell 7, Docker Desktop, and the repository root
as the current directory.

## Safety Boundary

- Query the public GoldSrc target through read-only A2S only.
- Never send RCON commands to a server you do not own.
- The command scene deliberately uses a server without an RCON port, so dispatch
  fails before any RCON network request.
- Use only short-lived Development JWTs and do not save them in the repository.

## Prepare Before The Demo

Confirm that the public target currently answers A2S. Public servers can change
without notice, so perform this check shortly before presenting:

```powershell
dotnet run --project .\src\GoldSrcOps.A2SSpike -- `
  server.csomod.com:27015 `
  --timeout 5000 `
  --encoding windows-1251
```

If it is unavailable, use a GoldSrc server you are authorized to query. The
deterministic incident scene below remains usable, but do not claim a successful
live A2S result that was not observed.

Create a one-hour Operator token, copy the emitted value, and start the local
stack in the first terminal:

```powershell
.\tools\dev\new-local-jwt.ps1 `
  -Name demo-operator `
  -Role Operator `
  -ValidFor 1h

.\tools\dev\start-local.ps1
```

In a second terminal, keep the token in memory without adding it to shell
history:

```powershell
$baseUrl = "http://localhost:5142"
$token = Read-Host "Operator JWT" -MaskInput
$headers = @{ Authorization = "Bearer $token" }
$demoSuffix = Get-Date -Format "yyyyMMdd-HHmmss"
```

## 1. Health And Authentication

Show that bounded health probes are anonymous while application data is
protected:

```powershell
(Invoke-WebRequest "$baseUrl/health/live").StatusCode
(Invoke-WebRequest "$baseUrl/health/ready").StatusCode
(Invoke-WebRequest "$baseUrl/api/servers" -SkipHttpErrorCheck).StatusCode
(Invoke-WebRequest "$baseUrl/api/servers" -Headers $headers).StatusCode
```

Expected status codes are `200`, `200`, `401`, and `200`. The Operator token can
perform mutations and all Reader operations; the fallback policy protects new
endpoints unless a narrower policy is selected explicitly.

## 2. Live A2S Polling And History

Register the preflighted public target with a short demo polling interval:

```powershell
$liveBody = @{
  name = "Demo live server $demoSuffix"
  host = "server.csomod.com"
  queryPort = 27015
  rconPort = $null
  pollIntervalSeconds = 5
  notes = "Temporary v1 demo target"
} | ConvertTo-Json

$liveServer = Invoke-RestMethod `
  -Method Post `
  -Uri "$baseUrl/api/servers" `
  -ContentType "application/json" `
  -Headers $headers `
  -Body $liveBody

$liveServer | Select-Object id, name, host, queryPort, isEnabled
```

Wait for the background worker, then show the current state and persisted
snapshot:

```powershell
$deadline = (Get-Date).AddSeconds(20)
do {
  Start-Sleep -Seconds 2
  $liveStatus = Invoke-RestMethod `
    "$baseUrl/api/servers/$($liveServer.id)/status" `
    -Headers $headers
} while ($null -eq $liveStatus.lastCheckedAtUtc -and (Get-Date) -lt $deadline)

$liveStatus | Format-List `
  status, isReachable, currentMap, players, maxPlayers, latencyMs, lastCheckedAtUtc

$liveHistory = Invoke-RestMethod `
  "$baseUrl/api/servers/$($liveServer.id)/snapshots?limit=3" `
  -Headers $headers
$liveHistory.items | Select-Object `
  checkedAtUtc, isReachable, map, players, maxPlayers, latencyMs
```

The useful point is the flow, not a particular map or player count: the poller
uses A2S, updates current state, and appends immutable history in PostgreSQL.

## 3. Deterministic Incident

Register an intentionally unreachable loopback UDP endpoint. This is local and
does not send traffic to a third party:

```powershell
$offlineBody = @{
  name = "Demo unavailable server $demoSuffix"
  host = "127.0.0.1"
  queryPort = 1
  rconPort = $null
  pollIntervalSeconds = 5
  notes = "Deterministic incident demo target"
} | ConvertTo-Json

$offlineServer = Invoke-RestMethod `
  -Method Post `
  -Uri "$baseUrl/api/servers" `
  -ContentType "application/json" `
  -Headers $headers `
  -Body $offlineBody

$deadline = (Get-Date).AddSeconds(45)
do {
  Start-Sleep -Seconds 2
  $incidents = @(
    Invoke-RestMethod `
      "$baseUrl/api/servers/$($offlineServer.id)/incidents" `
      -Headers $headers
  )
} while ($incidents.Count -eq 0 -and (Get-Date) -lt $deadline)

$offlineStatus = Invoke-RestMethod `
  "$baseUrl/api/servers/$($offlineServer.id)/status" `
  -Headers $headers
$offlineStatus | Format-List status, consecutiveFailures, failureReason
$incidents | Select-Object type, openedAtUtc, startReason, consecutiveFailures
```

The Development threshold is three consecutive failures. GoldSrcOps records an
availability incident; v1 does not claim alert delivery.

## 4. Durable Command Audit And Safe Failure

Queue a `say` command for the live A2S target. Its missing RCON port is
intentional:

```powershell
$commandBody = @{
  message = "GoldSrcOps v1 demo"
} | ConvertTo-Json

$queued = Invoke-RestMethod `
  -Method Post `
  -Uri "$baseUrl/api/servers/$($liveServer.id)/commands/say" `
  -ContentType "application/json" `
  -Headers $headers `
  -Body $commandBody

$deadline = (Get-Date).AddSeconds(10)
do {
  Start-Sleep -Milliseconds 500
  $execution = Invoke-RestMethod `
    "$baseUrl/api/commands/$($queued.id)" `
    -Headers $headers
} while ($execution.status -in @("Pending", "Running") -and (Get-Date) -lt $deadline)

$execution | Format-List `
  id, type, status, requestedBy, requestedAtUtc, completedAtUtc, failureReason
```

The command is persisted before dispatch, `requestedBy` comes from the JWT
subject, and the worker records `Failed` with `RCON port is not configured.`
No RCON packet is sent. Real RCON verification is restricted to an owned server
and the guarded `tools/smoke/rcon-live.ps1` helper.

## 5. Dashboard And Metrics

Finish with the operator view and telemetry emitted by the flows above:

```powershell
$overview = Invoke-RestMethod `
  "$baseUrl/api/dashboard/overview" `
  -Headers $headers
$overview | ConvertTo-Json -Depth 5

$metrics = (Invoke-WebRequest "$baseUrl/metrics" -Headers $headers).Content
($metrics -split "`n") | Where-Object {
  $_ -match "^goldsrcops_(polling|commands|snapshot_retention)"
} | Select-Object -First 20
```

This closes the story across authenticated operations, background work,
persistence, incidents, health, and OpenTelemetry Prometheus metrics.

## Finish

Disable both demo records so later local runs do not keep polling them:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri "$baseUrl/api/servers/$($liveServer.id)/disable" `
  -Headers $headers

Invoke-RestMethod `
  -Method Post `
  -Uri "$baseUrl/api/servers/$($offlineServer.id)/disable" `
  -Headers $headers
```

The records, snapshots, incident, and command execution remain in PostgreSQL as
the audit trail. Stop the API with `Ctrl+C`; the Docker database can remain
running for later inspection.

For deeper verification, use `docs/smoke-test.md`. For architecture and v1
scope evidence, use `docs/architecture.md` and `docs/v1-readiness.md`.
