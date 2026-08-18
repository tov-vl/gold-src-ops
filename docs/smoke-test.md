# GoldSrcOps Smoke Test

This smoke test exercises the local PostgreSQL-backed API against a live GoldSrc server.

## Fast Path

```powershell
dotnet user-jwts create `
  --project .\src\GoldSrcOps.Api `
  --name local-operator `
  --role Operator `
  --valid-for 1d

.\tools\dev\start-local.ps1
```

Keep the emitted token for the authenticated requests in the second terminal.
The token is for Development only and must not be committed.

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

### 3. Create A Local Operator Token

```powershell
dotnet user-jwts create `
  --project .\src\GoldSrcOps.Api `
  --name local-operator `
  --role Operator `
  --valid-for 1d
```

Keep the emitted token for the authenticated requests in the second terminal.

### 4. Run The API

```powershell
dotnet run --project .\src\GoldSrcOps.Api --launch-profile http
```

The HTTP profile listens on `http://localhost:5142`.

### 5. Check Health And Metrics

Open a second terminal:

```powershell
$baseUrl = "http://localhost:5142"
$token = "<token emitted by dotnet user-jwts>"
$headers = @{ Authorization = "Bearer $token" }

Invoke-RestMethod "$baseUrl/health/live"
Invoke-RestMethod "$baseUrl/health/ready"
((Invoke-WebRequest "$baseUrl/metrics" -Headers $headers).Content -split "`n") |
  Select-Object -First 10
```

### 6. Register A Live Server

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
  -Headers $headers `
  -Body $body

$server
```

### 7. Exercise Server Management

```powershell
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)" -Headers $headers

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
  -Headers $headers `
  -Body $patch

Invoke-RestMethod -Method Post -Uri "$baseUrl/api/servers/$($server.id)/disable" `
  -Headers $headers
Invoke-RestMethod -Method Post -Uri "$baseUrl/api/servers/$($server.id)/enable" `
  -Headers $headers
```

### 8. Queue And Observe A Command Safely

Commands are persisted first and then claimed automatically by the background
dispatcher. Without a configured RCON port and a resolvable local secret,
execution fails safely before sending anything to the server.

```powershell
$credential = @{
  secretAlias = "server_rcon"
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Put `
  -Uri "$baseUrl/api/servers/$($server.id)/credentials/rcon" `
  -ContentType "application/json" `
  -Headers $headers `
  -Body $credential

$command = @{
  message = "hello from GoldSrcOps"
} | ConvertTo-Json

$queued = Invoke-RestMethod `
  -Method Post `
  -Uri "$baseUrl/api/servers/$($server.id)/commands/say" `
  -ContentType "application/json" `
  -Headers $headers `
  -Body $command

$deadline = (Get-Date).AddSeconds(10)
do {
  Start-Sleep -Milliseconds 500
  $execution = Invoke-RestMethod "$baseUrl/api/commands/$($queued.id)" -Headers $headers
} while ($execution.status -in @("Pending", "Running") -and (Get-Date) -lt $deadline)

$execution
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/commands?limit=10" -Headers $headers
```

To execute a real RCON command, use only a server you control. Set `rconPort`
when registering or patching the server, and store the password under the
dedicated `RconSecrets:server_rcon` configuration key.

Configure it with Secret Manager:

```powershell
dotnet user-secrets set "RconSecrets:server_rcon" "<your-rcon-password>" `
  --project .\src\GoldSrcOps.Api
```

Or set the equivalent environment variable before starting the API process:

```powershell
$env:RconSecrets__server_rcon = "<your-rcon-password>"
```

Restart the API after changing its local secret source, then queue a new command
for dispatch. The selected alias is stored internally as
`rcon-secret://server_rcon`. Arbitrary `env://`, `config://`, and
`dev-secrets://` references are unsupported.

For real dispatch, prefer the guarded helper over manually posting a command.
First run its authenticated preflight:

```powershell
.\tools\smoke\rcon-live.ps1 `
  -ServerId $server.id `
  -AcknowledgeOwnedServer `
  -WhatIf
```

After verifying the displayed server name, host, RCON port, and generated
`say` command, remove `-WhatIf`. The helper requires the server id to be typed
again before it queues anything:

```powershell
.\tools\smoke\rcon-live.ps1 `
  -ServerId $server.id `
  -AcknowledgeOwnedServer
```

The helper prompts for the Operator JWT without echoing it and never reads the
RCON password. It polls the persisted command until completion and prints only
its identifiers, status, and elapsed time. If the helper times out, inspect the
command record before retrying because RCON execution is not idempotent.

### 9. Check Monitoring Reads

The background poller runs every few seconds. Wait briefly, then query status and history:

```powershell
Start-Sleep -Seconds 10

Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/status" -Headers $headers
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/snapshots?limit=10" -Headers $headers
Invoke-RestMethod "$baseUrl/api/dashboard/overview" -Headers $headers
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/incidents" -Headers $headers
```

Expected result:

- `/health/live` and `/health/ready` return healthy responses.
- `/metrics` exposes Prometheus metrics.
- Command metrics are exposed as `goldsrcops_commands_queued`,
  `goldsrcops_commands_dispatched`, `goldsrcops_commands_completed`, and
  `goldsrcops_commands_recovered`.
- `PATCH /api/servers/{id}` updates editable server settings.
- Disabled servers are skipped by polling, and re-enabled servers can be polled again.
- Credential responses report metadata only and do not echo the secret alias or canonical reference.
- Command responses derive `RequestedBy` from the authenticated token subject.
- Background dispatch transitions command status without leaking credential values.
- Missing local RCON configuration is reported as a safe command failure before network execution.
- `/status` eventually reports `Online` if the live server is reachable.
- `/snapshots` contains at least one poll attempt.
- `/dashboard/overview` includes the registered server in its counts.
- `/incidents` remains empty while polling succeeds.

If the live server is offline or blocks the query, the status can be `Offline` and an incident can open after the configured failure threshold. In that case, the smoke test still verifies the failure path.
