# GoldSrcOps Smoke Test

GoldSrcOps provides an isolated container-image smoke test and a longer local
workflow that exercises the PostgreSQL-backed API against a live GoldSrc
server.

## Container Image

Run the production image check from the repository root with PowerShell 7:

```powershell
pwsh -NoProfile -File .\tools\smoke\container.ps1
```

The script builds a uniquely tagged image, verifies its non-root runtime and
publish contents, checks that missing production configuration fails fast,
and proves that Production rejects an HTTP alert webhook. It starts an isolated
PostgreSQL container and applies EF Core migrations as a separate host-side
action. It then starts the API with alert delivery enabled, a read-only
filesystem, dropped Linux capabilities, and no-new-privileges before requiring
both `/health/live` and `/health/ready` to return `200 Healthy`. Finally, it
checks that the hosted alert dispatcher started and that its HTTPS endpoint and
synthetic authorization marker are absent from container logs.

The container smoke does not send an alert to an external endpoint. Synthetic
Kestrel tests cover the HTTP delivery boundary, while PostgreSQL integration
tests cover claiming, ordering, retry, dead-letter, recovery, and retention.

All temporary containers and the dedicated Docker network are removed in a
`finally` block. The temporary image tag is also removed by default; pass
`-KeepImage` only when it is needed for local troubleshooting.

GitHub Actions runs the same command in the dependent `Container Smoke` job
after the regular `Quality Gate` succeeds.

## Fast Path

```powershell
.\tools\dev\new-local-jwt.ps1
.\tools\dev\start-local.ps1
```

Keep the emitted token for the authenticated requests in the second terminal.
The token is for Development only and must not be committed. The command writes
local Bearer issuer/audience settings to ignored `appsettings.Local.json` and
keeps the signing key in User Secrets, so token creation does not modify tracked
configuration.

The script starts PostgreSQL, waits for readiness, restores solution packages
and local tools, applies EF Core migrations, and runs the API on
`http://localhost:5142`.

## Manual Path

### 1. Start Local Infrastructure

```powershell
docker compose -f .\ops\docker-compose.yml up -d postgres
```

PostgreSQL listens on `localhost:5432` with database/user/password `goldsrcops`.

### 2. Apply Migrations

```powershell
dotnet tool restore
dotnet tool run dotnet-ef -- database update `
  --project .\src\GoldSrcOps.Infrastructure `
  --startup-project .\src\GoldSrcOps.Api `
  -- --environment Development
```

### 3. Create A Local Operator Token

```powershell
.\tools\dev\new-local-jwt.ps1 `
  -Name local-operator `
  -Role Operator `
  -ValidFor 1d
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
- The safe command flow emits `goldsrcops_commands_queued` and
  `goldsrcops_commands_completed`. `goldsrcops_commands_dispatched` appears only
  when a command reaches the RCON executor; `goldsrcops_commands_recovered`
  appears only after interrupted-command recovery.
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
