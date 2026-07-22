# GoldSrcOps

GoldSrcOps is a backend control plane experiment for Counter-Strike 1.6 / GoldSrc dedicated servers.
The project now has a small A2S query spike plus the first ASP.NET Core backend skeleton around the production path.

## Current Status

- Created .NET solution under `D:\source\repos\personal\gold-src-ops`.
- Targeting .NET 10 LTS.
- Added `GoldSrcOps.A2SSpike`, a console app for `A2S_INFO` queries.
- Supports regular A2S info responses, challenge responses, Source-style responses, and GoldSrc-style responses.
- Supports configurable text encoding for legacy server names, for example `windows-1251`.
- Added the initial modular backend projects:
  - `GoldSrcOps.Api`
  - `GoldSrcOps.Contracts`
  - `GoldSrcOps.Application`
  - `GoldSrcOps.Domain`
  - `GoldSrcOps.Infrastructure`
- Added PostgreSQL Docker Compose setup under `ops/docker-compose.yml`.
- Added EF Core persistence and the initial migration.
- Added health endpoints and first server registration/status endpoints.
- Added an in-process polling service that queries enabled servers, updates current state, and writes poll snapshots.
- Added availability incident detection with open/close transitions after repeated polling failures.
- Added monitoring read endpoints for snapshot history and dashboard overview.
- Added readiness health checks that validate database connectivity.
- Added OpenTelemetry metrics export in Prometheus format.
- Added server edit and enable/disable endpoints.
- Added durable background command execution with atomic per-server claiming, interrupted-command recovery, local secret-reference resolution, and a live GoldSrc RCON client.
- Added command execution metrics for queued, dispatched, completed, and recovered command dispatches.
- Added PostgreSQL integration coverage for concurrent command claims and interrupted-command recovery.
- Added JWT bearer authentication with `Reader` and `Operator` authorization policies and token-derived command audit identity.
- Added focused unit tests for polling incident transitions, monitoring read aggregation, A2S packet parsing, and server state transitions.
- Added lightweight API integration tests for server registration, status reads, snapshot history, and dashboard overview.
- Added deterministic polling integration tests with fake A2S query responses and EF-backed repositories.
- Added PostgreSQL-backed integration tests with Testcontainers.

## Architecture Overview

GoldSrcOps is a modular monolith with separate API, contracts, application, domain, and infrastructure projects.
The API host exposes HTTP endpoints, health checks, metrics, and the in-process polling worker.
Application services coordinate use cases, domain entities own state transitions, and infrastructure implements EF Core persistence plus GoldSrc A2S integration.

See [docs/architecture.md](docs/architecture.md) for the component diagram and runtime flows.

## Quick Local Start

```powershell
dotnet user-jwts create `
  --project .\src\GoldSrcOps.Api `
  --name local-operator `
  --role Operator `
  --valid-for 1d

.\tools\dev\start-local.ps1
```

The first command configures the Development bearer scheme and prints a local
Operator token. Keep the token for the smoke flow, but do not store it in the
repository.

The script starts PostgreSQL, waits for the container healthcheck, restores local .NET tools, applies EF Core migrations, and runs the API on `http://localhost:5142`.
It prefers the repository-local SDK under `.\.dotnet\dotnet.exe` when present.

Useful variants:

```powershell
.\tools\dev\start-local.ps1 -NoRun
.\tools\dev\start-local.ps1 -SkipDocker -SkipToolRestore -SkipMigrations
```

Use `-NoRun` to prepare local dependencies and migrations without starting the API.
Use the skip flags when Docker, tools, or migrations are already prepared.

## Manual Local Start

### 1. Start Local Infrastructure

```powershell
docker compose -f .\ops\docker-compose.yml up -d postgres
```

PostgreSQL listens on `localhost:5432` with database/user/password `goldsrcops`.
If you also want pgAdmin, run `docker compose -f .\ops\docker-compose.yml up -d pgadmin`.
pgAdmin is then available on `http://localhost:5050`.

### 2. Apply Database Migrations

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

Keep the emitted token for authenticated local requests. This command is for
Development only; production deployments must use an external OAuth 2.0 or
OpenID Connect provider.

### 4. Run The API

```powershell
dotnet run --project .\src\GoldSrcOps.Api --launch-profile http
```

The HTTP launch profile listens on `http://localhost:5142`.
Polling runs inside the API host by default. Configuration lives under `Polling` in `appsettings.json`:

- `Enabled`
- `LoopDelaySeconds`
- `QueryTimeoutMilliseconds`
- `BatchSize`
- `IncidentFailureThreshold`

Pending commands are executed by a background worker. Configuration lives under
`CommandDispatcher`:

- `Enabled`
- `LoopDelayMilliseconds`
- `MaxConcurrency`
- `InterruptedAfterSeconds`
- `RecoveryIntervalSeconds`

RCON dispatch configuration lives under `Rcon`:

- `TimeoutMilliseconds`
- `MaxResponseLength`

See [docs/rcon.md](docs/rcon.md) for secret-reference formats, dispatch flow,
and current RCON limits.

All control-plane API endpoints require an authenticated bearer token. Read
endpoints and `/metrics` accept `Reader` or `Operator`; mutations require
`Operator`. Liveness and readiness remain anonymous. See
[docs/security.md](docs/security.md) for the complete policy matrix and
production configuration requirements.

Initial endpoints:

- `GET /health/live` - lightweight liveness probe.
- `GET /health/ready` - readiness probe that validates database connectivity.
- `GET /metrics` - Prometheus scrape endpoint backed by OpenTelemetry metrics.
- `POST /api/servers`
- `GET /api/servers`
- `GET /api/servers/{id}`
- `PATCH /api/servers/{id}`
- `POST /api/servers/{id}/enable`
- `POST /api/servers/{id}/disable`
- `PUT /api/servers/{id}/credentials/rcon`
- `GET /api/servers/{id}/credentials`
- `POST /api/servers/{id}/commands/change-map`
- `POST /api/servers/{id}/commands/restart`
- `POST /api/servers/{id}/commands/say`
- `POST /api/servers/{id}/commands/raw`
- `GET /api/servers/{id}/commands?limit=`
- `GET /api/commands/{commandId}`
- `GET /api/servers/{id}/status`
- `GET /api/servers/{id}/snapshots?from=&to=&limit=`
- `GET /api/servers/{id}/incidents`
- `GET /api/dashboard/overview`
- `GET /api/incidents/open`
- `GET /api/incidents/{id}`

After registering a server, the background poller will update `/api/servers/{id}/status` once the next polling pass succeeds.
After repeated failed polls, the poller opens an availability incident. A later successful poll closes it.
Queued commands are claimed and executed automatically. Workers may process
different servers in parallel, but PostgreSQL serialization permits only one
`Running` command per server. Interrupted commands are failed without an
automatic retry because RCON commands are not idempotent.

`/metrics` exposes ASP.NET Core, runtime, and GoldSrcOps application metrics in Prometheus format.
Application metrics cover polling runs, server poll attempts by result, incident transitions, queued commands,
dispatched commands, completed command dispatches by result, and recovered interrupted commands.
The Prometheus ASP.NET Core exporter is currently referenced as a prerelease OpenTelemetry package because a stable exporter package is not available yet.

## Local Smoke Flow

After the API is running:

```powershell
$baseUrl = "http://localhost:5142"
$token = "<token emitted by dotnet user-jwts>"
$headers = @{ Authorization = "Bearer $token" }

Invoke-RestMethod "$baseUrl/health/live"
Invoke-RestMethod "$baseUrl/health/ready"
((Invoke-WebRequest "$baseUrl/metrics" -Headers $headers).Content -split "`n") |
  Select-Object -First 10
```

Register a live server:

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

Queue a command without a local secret. The background dispatcher fails it
safely unless both an RCON port and a resolvable secret reference are configured:

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

To execute a real RCON command, use a server you control, set `rconPort`, and
store its password under the dedicated `RconSecrets:server_rcon` configuration
key. With Secret Manager:

```powershell
dotnet user-secrets set "RconSecrets:server_rcon" "<your-rcon-password>" `
  --project .\src\GoldSrcOps.Api
```

Or set the equivalent environment variable before starting the API:

```powershell
$env:RconSecrets__server_rcon = "<your-rcon-password>"
```

Both sources resolve the API alias `server_rcon` to the internal canonical
reference `rcon-secret://server_rcon`. Arbitrary `env://`, `config://`, and
`dev-secrets://` references are intentionally rejected.

Never run the command dispatch smoke against a public server you do not own.

Continue with monitoring reads:

```powershell
Start-Sleep -Seconds 10

Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/status" -Headers $headers
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/snapshots?limit=10" -Headers $headers
Invoke-RestMethod "$baseUrl/api/dashboard/overview" -Headers $headers
Invoke-RestMethod "$baseUrl/api/servers/$($server.id)/incidents" -Headers $headers
```

## Run Tests

```powershell
dotnet test
```

## Code Quality

The solution uses .NET analyzers, Meziantou.Analyzer, and `.editorconfig` rules through `Directory.Build.props`.

```powershell
dotnet restore GoldSrcOps.sln -p:AuditPipeline=true
dotnet format GoldSrcOps.sln --verify-no-changes
dotnet build GoldSrcOps.sln
dotnet test GoldSrcOps.sln
dotnet list GoldSrcOps.sln package --vulnerable --include-transitive
```

GitHub Actions runs the same quality gate on every push and pull request.

## Smoke Test

See `docs/smoke-test.md` for a Docker-based local smoke test that applies migrations, runs the API, registers a live GoldSrc server, and checks status, snapshots, dashboard overview, and incidents.

## Run The Spike

```powershell
dotnet run --project .\src\GoldSrcOps.A2SSpike -- server.csomod.com:27015 --timeout 5000 --encoding windows-1251
```

You can also pass host and port separately:

```powershell
dotnet run --project .\src\GoldSrcOps.A2SSpike -- 217.156.22.86 27015
```

## Expected Output

```text
Server:      [ZOMBIES]+[CSO MOD] [#1] CSOMOD.COM [since 2012]
Endpoint:    server.csomod.com:27015
Engine:      Source
Map:         zm_csdark_cinder
Players:     28/32 (2 bots)
Folder:      cstrike
Game:        Zombie Plague [CSO]
Protocol:    48
Type:        d
Environment: l
Private:     False
VAC:         False
Version:     1.1.2.7/Stdio
Latency:     140 ms
```

The exact map, player count, and latency will vary.

## Protocol Reference

The spike follows Valve's documented A2S server query format:

- https://developer.valvesoftware.com/wiki/Server_queries?uselang=en

## Next Milestone

Add command dispatch operational hardening: per-server execution serialization, structured command logs, and a guarded local smoke script for owned servers.
