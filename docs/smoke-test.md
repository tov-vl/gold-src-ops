# GoldSrcOps Smoke Test

This smoke test exercises the local PostgreSQL-backed API against a live GoldSrc server.

## Fast Path

```powershell
.\tools\dev\start-local.ps1
```

The script starts PostgreSQL, waits for readiness, applies EF Core migrations, and runs the API on `http://localhost:5142`.

## Manual Path

### 1. Start Local Infrastructure

```powershell
docker compose -f .\ops\docker-compose.yml up -d postgres
```

PostgreSQL listens on `localhost:5432` with database/user/password `goldsrcops`.

### 2. Apply Migrations

```powershell
dotnet tool restore
dotnet tool run dotnet-ef database update `
  --project .\src\GoldSrcOps.Infrastructure `
  --startup-project .\src\GoldSrcOps.Api `
  -- --environment Development
```

### 3. Run The API

```powershell
dotnet run --project .\src\GoldSrcOps.Api --launch-profile http
```

The HTTP profile listens on `http://localhost:5142`.

### 4. Check Health And Metrics

Open a second terminal:

```powershell
$baseUrl = "http://localhost:5142"

Invoke-RestMethod "$baseUrl/health/live"
Invoke-RestMethod "$baseUrl/health/ready"
((Invoke-WebRequest "$baseUrl/metrics").Content -split "`n") | Select-Object -First 10
```

### 5. Register A Live Server

In the same second terminal:

```powershell
$body = @{
  name = "CSOMOD Zombie Server"
  host = "server.csomod.com"
  queryPort = 27015
  rconPort = $null
  pollIntervalSeconds = 30
  notes = "Live smoke test target"
} | ConvertTo-Json

$server = Invoke-RestMethod `
  -Method Post `
  -Uri "$baseUrl/api/servers" `
  -ContentType "application/json" `
  -Body $body

$server
```

### 6. Exercise Server Management

```powershell
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)"

$patch = @{
  name = "CSOMOD Zombie Server"
  host = "server.csomod.com"
  queryPort = 27015
  rconPort = $null
  pollIntervalSeconds = 45
  notes = "Updated smoke test target"
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Patch `
  -Uri "$baseUrl/api/servers/$($server.id)" `
  -ContentType "application/json" `
  -Body $patch

Invoke-RestMethod -Method Post -Uri "$baseUrl/api/servers/$($server.id)/disable"
Invoke-RestMethod -Method Post -Uri "$baseUrl/api/servers/$($server.id)/enable"
```

### 7. Queue And Dispatch A Command Safely

Commands are persisted first and then dispatched through the configured command executor.
The default local executor is intentionally not connected to live RCON yet, so dispatch marks the command as `Failed` with `RCON executor is not configured.`

```powershell
$credential = @{
  secretReference = "dev-secrets://goldsrcops/server/rcon"
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Put `
  -Uri "$baseUrl/api/servers/$($server.id)/credentials/rcon" `
  -ContentType "application/json" `
  -Body $credential

$command = @{
  message = "hello from GoldSrcOps"
  requestedBy = "local-smoke"
} | ConvertTo-Json

$queued = Invoke-RestMethod `
  -Method Post `
  -Uri "$baseUrl/api/servers/$($server.id)/commands/say" `
  -ContentType "application/json" `
  -Body $command

$queued
Invoke-RestMethod -Method Post -Uri "$baseUrl/api/commands/$($queued.id)/dispatch"
Invoke-RestMethod "$baseUrl/api/commands/$($queued.id)"
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/commands?limit=10"
```

### 8. Check Monitoring Reads

The background poller runs every few seconds. Wait briefly, then query status and history:

```powershell
Start-Sleep -Seconds 10

Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/status"
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/snapshots?limit=10"
Invoke-RestMethod "$baseUrl/api/dashboard/overview"
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/incidents"
```

Expected result:

- `/health/live` and `/health/ready` return healthy responses.
- `/metrics` exposes Prometheus metrics.
- `PATCH /api/servers/{id}` updates editable server settings.
- Disabled servers are skipped by polling, and re-enabled servers can be polled again.
- Credential responses report metadata only and do not echo `secretReference`.
- Command dispatch transitions command status without leaking credential values.
- The default local command executor reports that live RCON dispatch is not configured yet.
- `/status` eventually reports `Online` if the live server is reachable.
- `/snapshots` contains at least one poll attempt.
- `/dashboard/overview` includes the registered server in its counts.
- `/incidents` remains empty while polling succeeds.

If the live server is offline or blocks the query, the status can be `Offline` and an incident can open after the configured failure threshold. In that case, the smoke test still verifies the failure path.
