# GoldSrcOps

GoldSrcOps is a production-minded .NET backend control plane for monitoring and
administering Counter-Strike 1.6 and other GoldSrc dedicated servers. It polls
servers through A2S, records availability history and incidents, executes
auditable operator actions through RCON, and exposes health and telemetry for
operations.

**Status:** [v2.3.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.3.0)
is the current public release. Its signed annotated tag identifies application
revision `58a74da` and promotes the exact digest exercised by the
`v2.3.0-rc.5` reference deployment. Final evidence
[pull request #68](https://github.com/tov-vl/gold-src-ops/pull/68), publication
hardening [pull request #69](https://github.com/tov-vl/gold-src-ops/pull/69),
their post-merge workflows
[#33784710906](https://github.com/tov-vl/gold-src-ops/actions/runs/33784710906)
and
[#33787856532](https://github.com/tov-vl/gold-src-ops/actions/runs/33787856532),
and the final publication workflow
[#33788658773](https://github.com/tov-vl/gold-src-ops/actions/runs/33788658773)
all passed. Release notes and detailed evidence are available in
[docs/release-notes-v2.3.md](docs/release-notes-v2.3.md) and
[docs/v2.3-readiness.md](docs/v2.3-readiness.md).

Active v2.4 work begins with independent external availability measurement and
a prospective `API-01` window before the public and authenticated operator web
experience. The provider-independent contract is documented in
[docs/v2.4-external-availability-monitoring.md](docs/v2.4-external-availability-monitoring.md),
and the conditional managed-provider choice is recorded in
[docs/v2.4-synthetic-monitoring-provider-decision.md](docs/v2.4-synthetic-monitoring-provider-decision.md).

## Highlights

- Scheduled `A2S_INFO` polling with challenge handling and legacy
  `windows-1251` text support.
- PostgreSQL-backed current state, immutable snapshots, and availability
  incident transitions.
- Transactional incident-alert outbox with at-least-once HTTPS webhook
  delivery, bounded retries, dead letters, and backlog telemetry.
- Reader API for bounded dead-letter inspection plus audited, idempotent
  single-message replay restricted to Operators.
- JWT bearer authentication with explicit `Reader` and `Operator` policies.
- Durable RCON command queue with per-server serialization, interrupted-command
  recovery, bounded multi-datagram responses, external secret references, and
  payload-safe lifecycle logs.
- Liveness, database readiness, structured logging, authenticated Prometheus
  metrics, and a private OTLP Collector, Prometheus, and Grafana path.
- Bounded snapshot retention with PostgreSQL integration and concurrency tests.
- Tag-gated immutable GHCR image publication with OCI metadata and
  digest-based container verification.
- Repeatable Docker-based local startup, authenticated smoke test, and guarded
  owned-server RCON verification.

## Architecture Overview

GoldSrcOps is a modular monolith with separate API, contracts, application, domain, and infrastructure projects.
The API host exposes HTTP endpoints, health checks, metrics, and the in-process
polling, command-dispatch, alert-dispatch, and snapshot-retention workers.
Application services coordinate use cases, domain entities own state transitions, and infrastructure implements EF Core persistence plus GoldSrc A2S integration.

See [docs/architecture.md](docs/architecture.md) for the component diagram and
runtime flows. The MVP evidence and accepted deferrals are recorded in
[docs/v1-readiness.md](docs/v1-readiness.md). A presenter-oriented walkthrough
is available in [docs/demo.md](docs/demo.md), and the release-facing summary is
in [docs/release-notes-v1.md](docs/release-notes-v1.md). The focused v1.1
operability delta is covered by
[docs/v1.1-readiness.md](docs/v1.1-readiness.md). The v2 alert-delivery release
is summarized in
[docs/release-notes-v2.md](docs/release-notes-v2.md). The compatible v2.1
dead-letter recovery release is summarized in
[docs/release-notes-v2.1.md](docs/release-notes-v2.1.md). The v2.2 RCON
reliability release is summarized in
[docs/release-notes-v2.2.md](docs/release-notes-v2.2.md).
The published v2.3 reference-deployment release is summarized in
[docs/release-notes-v2.3.md](docs/release-notes-v2.3.md). Its bounded
operational gate, repository checks, digest-preserving stable publication, and
published-image verification have passed.

## Documentation

| Topic | Document |
| --- | --- |
| Five-to-ten-minute walkthrough | [Demo guide](docs/demo.md) |
| Delivered scope and release limits | [v1 release notes](docs/release-notes-v1.md) |
| v1.1 operability release | [v1.1 release notes](docs/release-notes-v1.1.md) |
| v2 alert-delivery release | [v2 release notes](docs/release-notes-v2.md) |
| v2.1 dead-letter recovery release | [v2.1 release notes](docs/release-notes-v2.1.md) |
| v2.2 RCON reliability release | [v2.2 release notes](docs/release-notes-v2.2.md) |
| v2.3 reference deployment release | [v2.3 release notes](docs/release-notes-v2.3.md) |
| Components and runtime flows | [Architecture](docs/architecture.md) |
| Design trade-offs | [Architecture decisions](docs/architecture-decisions.md) |
| Completed v2.3 reference deployment | [v2.3 production deployment](docs/v2.3-production-deployment.md) |
| v2.3 release evidence | [v2.3 readiness](docs/v2.3-readiness.md) |
| Initial SLI record and SLO proposal | [Service-level objectives](docs/service-level-objectives.md) |
| v2.4 external availability contract | [External availability monitoring](docs/v2.4-external-availability-monitoring.md) |
| v2.4 synthetic-monitoring provider | [Synthetic-monitoring provider decision](docs/v2.4-synthetic-monitoring-provider-decision.md) |
| v2.3 production Compose contract | [Reference production Compose](ops/production/README.md) |
| Production metrics path and operations | [Observability](docs/observability.md) |
| PostgreSQL off-host backup and restore | [PostgreSQL backup](docs/postgresql-backup.md) |
| v2.3 game-server baseline | [Controlled game-server baseline](docs/v2.3-controlled-gameserver-baseline.md) |
| v2.3 game-server host foundation | [Game-server host bootstrap](ops/gameserver/README.md) |
| v2.3 game-server provider decision | [Game-server provider decision](docs/v2.3-gameserver-provider-decision.md) |
| v2 alert delivery and transactional outbox | [v2 outbox design](docs/v2-alert-outbox.md) |
| Alert delivery configuration and recovery | [Alert delivery operations](docs/alert-delivery.md) |
| Dead-letter inspection and replay contract | [Dead-letter replay design](docs/dead-letter-replay.md) |
| Authentication and endpoint policies | [Security](docs/security.md) |
| Vulnerability reporting | [Security policy](.github/SECURITY.md) |
| Container rollout, migrations, and rollback | [Deployment](docs/deployment.md) |
| RCON safety and recovery | [RCON operations](docs/rcon.md) |
| v2.2 RCON response reliability | [RCON response design](docs/v2.2-rcon-response-reliability.md) |
| Full local verification | [Smoke test](docs/smoke-test.md) |
| MVP evidence | [v1 readiness](docs/v1-readiness.md) |
| v1.1 release evidence | [v1.1 readiness](docs/v1.1-readiness.md) |
| v2 alert delivery evidence | [v2 readiness](docs/v2-readiness.md) |
| v2.1 dead-letter recovery evidence | [v2.1 readiness](docs/v2.1-readiness.md) |
| v2.2 RCON reliability evidence | [v2.2 readiness](docs/v2.2-readiness.md) |

## Prerequisites

- .NET 10 SDK compatible with the repository `global.json`.
- Docker Desktop or another Docker Engine with Compose support.
- PowerShell 7 or Windows PowerShell 5.1 for the local helper scripts.
- Network access to a GoldSrc server only when running a live A2S demo.

## Quick Local Start

```powershell
.\tools\dev\new-local-jwt.ps1
.\tools\dev\start-local.ps1
```

The first command configures the Development bearer scheme and prints a local
Operator token. Keep the token for the smoke flow, but do not store it in the
repository. Local Bearer issuer/audience settings are written to ignored
`appsettings.Local.json`; the signing key remains in the project's User Secrets
store.

The script starts PostgreSQL, waits for the container healthcheck, restores
solution packages and local .NET tools, applies EF Core migrations, and runs the
API on `http://localhost:5142`.
It prefers the repository-local SDK under `.\.dotnet\dotnet.exe` when present.

Useful variants:

```powershell
.\tools\dev\start-local.ps1 -NoRun
.\tools\dev\start-local.ps1 -SkipDocker -SkipRestore -SkipToolRestore -SkipMigrations
```

Use `-NoRun` to prepare local dependencies and migrations without starting the API.
Use the skip flags when Docker, solution packages, tools, or migrations are already prepared.

## Manual Local Start

### 1. Start Local Infrastructure

```powershell
docker compose -f .\ops\docker-compose.yml up -d postgres
```

PostgreSQL listens on `localhost:5432` with database/user/password `goldsrcops`.
If you also want pgAdmin, run `docker compose -f .\ops\docker-compose.yml up -d pgadmin`.
pgAdmin is then available on `http://localhost:5050`.

These credentials are intentionally weak local Development defaults. Never
reuse them outside the disposable Docker environment.

The matching connection string exists only in `appsettings.Development.json`.
Every non-Development deployment must provide `ConnectionStrings__GoldSrcOps`
through its deployment configuration or secret store.

### 2. Apply Database Migrations

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

v1 requires exactly one process with `Polling:Enabled=true`. When running
multiple API instances, set `Polling__Enabled=false` on every HTTP-only replica.
Command dispatch does not share this restriction because PostgreSQL coordinates
its claims independently.

Pending commands are executed by a background worker. Configuration lives under
`CommandDispatcher`:

- `Enabled`
- `LoopDelayMilliseconds`
- `MaxConcurrency`
- `InterruptedAfterSeconds`
- `RecoveryIntervalSeconds`

Expired poll snapshots are removed by a separate bounded background worker.
Configuration lives under `SnapshotRetention`:

- `Enabled`
- `RetentionDays`
- `CleanupIntervalSeconds`
- `BatchSize`

Each pass deletes at most one batch and never modifies current server state or
incident history. See [docs/snapshot-retention.md](docs/snapshot-retention.md)
for validated ranges, cutoff semantics, metrics, and deployment guidance.

RCON dispatch configuration lives under `Rcon`:

- `TimeoutMilliseconds`
- `MaxResponseLength`
- `ResponseDrainMilliseconds`
- `MaxResponseDatagrams`
- `MaxResponseBytes`

See [docs/rcon.md](docs/rcon.md) for secret-reference formats, dispatch flow,
validated receive bounds, and current RCON limits.

All control-plane API endpoints require an authenticated bearer token. Read
endpoints and `/metrics` accept `Reader` or `Operator`; mutations require
`Operator`. Liveness and readiness remain anonymous. See
[docs/security.md](docs/security.md) for the complete policy matrix and
production configuration requirements.

API endpoints:

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

`/metrics` exposes ASP.NET Core, runtime, and GoldSrcOps application metrics in
Prometheus format. Application metrics cover polling runs, server poll attempts
by result, incident transitions, alert enqueueing, delivery, retry, dead-letter,
backlog, and replay outcomes, queued, dispatched, completed, and recovered
commands, plus snapshot-retention runs, deletions, failures, and duration.
Dead-letter replay uses only the bounded `accepted`, `idempotent`, `conflict`,
and `invalid` result labels; request IDs, event IDs, subjects, and reasons never
become metric dimensions.

The OpenTelemetry SDK, instrumentations, and stable OTLP exporter are aligned on
`1.18.0`; the direct ASP.NET Core Prometheus exporter remains
`1.18.0-beta.1` because no stable release exists. OTLP metrics export is
disabled by default for development and tests and enabled by the production
Compose contract with validated endpoint, protocol, export interval, and
timeout settings. Production Prometheus scrapes only the Collector; the
authenticated `/metrics` contract remains available during the v2 transition
for compatibility and rollback. The accepted prerelease risk and migration
path are recorded in Architecture Decisions 12 and 17.

For a private gRPC Collector endpoint, enable the exporter with:

```text
Telemetry__Otlp__Enabled=true
Telemetry__Otlp__Endpoint=http://otel-collector:4317
Telemetry__Otlp__Protocol=grpc
Telemetry__Otlp__ExportIntervalMilliseconds=60000
Telemetry__Otlp__ExportTimeoutMilliseconds=30000
```

`http/protobuf` is also supported. The endpoint must be an absolute HTTP or
HTTPS URL without user information, query, or fragment. Export timeout cannot
exceed the export interval.

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

For real dispatch, use the guarded helper and run `-WhatIf` before removing it:

```powershell
.\tools\smoke\rcon-live.ps1 `
  -ServerId $server.id `
  -AcknowledgeOwnedServer `
  -WhatIf
```

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

The full suite includes PostgreSQL Testcontainers tests and requires a running
Docker engine. Without Docker, run the non-PostgreSQL subset with:

```powershell
dotnet test --filter "Category!=PostgreSqlIntegration"
```

## Code Quality

The solution uses .NET analyzers, Meziantou.Analyzer, and `.editorconfig` rules through `Directory.Build.props`.

```powershell
dotnet restore GoldSrcOps.sln -p:AuditPipeline=true
dotnet format GoldSrcOps.sln --verify-no-changes --no-restore
dotnet build GoldSrcOps.sln --no-restore
dotnet test GoldSrcOps.sln --no-build
dotnet list GoldSrcOps.sln package --vulnerable --include-transitive
```

GitHub Actions runs the same quality gate on every push and pull request. After
it succeeds, a dependent `Container Smoke` job first validates the plan-only
control-plane and game-server host bootstraps plus deterministic host-readiness
and soak-readiness pass/fail decisions, then builds the production image and
applies its embedded migration bundle twice to isolated PostgreSQL. It checks
runtime hardening,
alert configuration, log-safety, health contracts, the private
API-to-Collector-to-Prometheus metric path, provisioned Grafana assets,
Collector-outage readiness, and an encrypted backup, full repository data
check, and isolated restore rehearsal. An exact
signed annotated `v<major>.<minor>.<patch>` or
`v<major>.<minor>.<patch>-rc.<number>` tag adds `Publish Image` and
`Verify Published Image`: they publish immutable exact and full-revision tags to
GHCR, record the digest, and rerun the smoke flow against the pulled digest. An
RC tag is explicitly classified as a release candidate and does not create a
stable release. The workflow never publishes `latest`; a stable tag can promote
the already verified digest of a matching RC revision without rebuilding it.
A manual workflow dispatch can resume an incomplete stable publication only
for an existing annotated stable tag. An existing GHCR reference is accepted
when it already matches the verified digest, or recovered before the GitHub
Release exists when it is a one-manifest wrapper around exactly that digest;
every other attempted overwrite fails closed.

The active `main` ruleset requires signed commits, linear history, the
`Quality Gate`, and `Container Smoke`, and blocks branch deletion and force
pushes. Candidate commits must first be pushed to another branch so CI can
report the required checks before `main` is updated. Pull requests are the
intended workflow, although the ruleset does not currently require one.

## Smoke Test

Run `pwsh -NoProfile -File .\tools\smoke\container.ps1` to build and verify the
production container against an isolated PostgreSQL instance. See
`docs/smoke-test.md` for the image flow, the separate control-plane and
game-server host-bootstrap, runtime-installer, and runtime-activation smokes,
the host-preflight and soak-readiness smokes, and the longer live GoldSrc server
flow. Live
control-plane hardening and auditing are documented in
`ops/production/README.md`; the game-host foundation, pinned runtime
installation, and guarded first-start workflow are in
`ops/gameserver/README.md`.
Use `docs/deployment.md` for image versioning, production configuration,
migrations, probes, and rollback. PostgreSQL recovery operations are in
`docs/postgresql-backup.md`; alert-specific rollout and recovery guidance is in
`docs/alert-delivery.md`. The active release-evidence boundary is recorded in
`docs/v2.3-readiness.md`.

For a concise five-to-ten-minute portfolio walkthrough, use `docs/demo.md`.

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

## Release Status

The public repository is configured with private vulnerability reporting,
Dependabot security updates, and a protected `main` workflow. The
[GoldSrcOps v2.3.0 release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.3.0)
adds the first continuously operated reference deployment across separate
control-plane and game-server hosts, public TLS and external OIDC, encrypted
off-host recovery, a private OpenTelemetry path, and bounded recovery and soak
evidence. Its signed stable tag promotes the exact image digest exercised by
the release candidate; no image was rebuilt after the soak. Detailed evidence
and explicit claim limits are recorded in
[docs/v2.3-readiness.md](docs/v2.3-readiness.md), while
[v2.2.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.2.0) is the
preceding stable release.

## License

GoldSrcOps is licensed under the [MIT License](LICENSE).
