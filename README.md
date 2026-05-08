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
- Added focused unit tests for polling incident transitions, monitoring read aggregation, A2S packet parsing, and server state transitions.
- Added lightweight API integration tests for server registration, status reads, snapshot history, and dashboard overview.

## Run Local Infrastructure

```powershell
docker compose -f .\ops\docker-compose.yml up -d
```

PostgreSQL listens on `localhost:5432` with database/user/password `goldsrcops`.
pgAdmin is available on `http://localhost:5050`.

## Apply Database Migrations

```powershell
dotnet tool restore
dotnet tool run dotnet-ef database update --project .\src\GoldSrcOps.Infrastructure --startup-project .\src\GoldSrcOps.Api
```

## Run The API

```powershell
dotnet run --project .\src\GoldSrcOps.Api
```

Polling runs inside the API host by default. Configuration lives under `Polling` in `appsettings.json`:

- `Enabled`
- `LoopDelaySeconds`
- `QueryTimeoutMilliseconds`
- `BatchSize`
- `IncidentFailureThreshold`

Initial endpoints:

- `GET /health/live`
- `GET /health/ready`
- `POST /api/servers`
- `GET /api/servers`
- `GET /api/servers/{id}`
- `GET /api/servers/{id}/status`
- `GET /api/servers/{id}/snapshots?from=&to=&limit=`
- `GET /api/servers/{id}/incidents`
- `GET /api/dashboard/overview`
- `GET /api/incidents/open`
- `GET /api/incidents/{id}`

After registering a server, the background poller will update `/api/servers/{id}/status` once the next polling pass succeeds.
After repeated failed polls, the poller opens an availability incident. A later successful poll closes it.

## Run Tests

```powershell
dotnet test
```

## Code Quality

The solution uses .NET analyzers, Meziantou.Analyzer, and `.editorconfig` rules through `Directory.Build.props`.

```powershell
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

Add readiness health checks, metrics, and more API integration coverage.
