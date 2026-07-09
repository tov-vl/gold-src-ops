# GoldSrcOps Backlog

This backlog tracks completed milestones and the next reviewable development or
release steps.

## Current Status

Completed:

- Repository created at `D:\source\repos\personal\gold-src-ops`.
- Solution created.
- A2S spike implemented in `src/GoldSrcOps.A2SSpike`.
- `A2S_INFO` live query verified.
- README and spike documentation added.
- ASP.NET Core API skeleton added.
- Domain/Application/Contracts/Infrastructure projects added.
- A2S client moved into Infrastructure behind `IGoldSrcServerQueryClient`.
- Initial domain entities added.
- PostgreSQL Docker Compose file added.
- EF Core DbContext and initial migration added.
- Health endpoints and initial server registration/status endpoints added.
- Background polling service added.
- Successful and failed poll attempts update `ServerCurrentState`.
- Every poll attempt writes a `PollSnapshot`.
- Availability incident detection added.
- `GET /api/incidents/open`, `GET /api/incidents/{id}`, and `GET /api/servers/{id}/incidents` added.
- Unit tests added for incident open/close transitions.
- Code style and static analysis configured through `.editorconfig`, `Directory.Build.props`, and Meziantou.Analyzer.
- `GET /api/servers/{id}/snapshots?from=&to=&limit=` added.
- `GET /api/dashboard/overview` added.
- Unit tests added for monitoring read aggregation and snapshot query defaults.
- Integration tests added for `POST /api/servers` and `GET /api/servers/{id}/status`.
- Unit tests added for A2S packet parsing with captured byte arrays.
- Unit tests added for core server state transition rules.
- GitHub Actions CI added for format, build, test, and package vulnerability checks.
- Docker-based smoke-test notes added for polling against a live server.
- API integration tests added for snapshot history and dashboard overview.
- Readiness health check validates database connectivity.
- `GET /metrics` exposes ASP.NET Core, runtime, and application polling metrics in Prometheus format.
- Deterministic polling integration tests added with fake A2S query responses and EF-backed repositories.
- Integration tests cover incident opening after repeated polling failures.
- Architecture overview and runtime flow diagrams added to `docs/architecture.md`.
- PostgreSQL-backed integration tests added with Testcontainers and EF Core migrations.
- `PATCH /api/servers/{id}` added for editing server connection details and polling settings.
- `POST /api/servers/{id}/enable` and `POST /api/servers/{id}/disable` added.
- Disabled servers are skipped by background polling and covered by deterministic integration tests.
- Local startup and migration workflow documented in README and smoke-test docs.
- `tools/dev/start-local.ps1` added for local PostgreSQL startup, EF migration, and API launch.
- `ServerCredential` added with external secret references instead of persisted plaintext secrets.
- `CommandExecution` added with command type, status, payload, requester, and execution timestamps.
- Command and credential endpoints added for RCON credential metadata, queuing commands, and reading command history.
- PostgreSQL migration added for `server_credentials` and `command_executions`.
- Unit, API integration, and PostgreSQL-backed integration tests added for the command foundation.
- `IRconCommandExecutor` boundary added for safe command dispatch.
- `POST /api/commands/{commandId}/dispatch` added to transition queued commands through `Running` into `Succeeded` or `Failed`.
- Deterministic tests cover successful fake dispatch, executor failure, timeout, missing RCON port, repeated dispatch conflict, and PostgreSQL status persistence.
- Local secret-reference resolution added for `env://`, `config://`, and `dev-secrets://` references.
- Live GoldSrc RCON client added behind `IRconCommandExecutor` with challenge/command handling, timeout mapping, authentication failure handling, and sanitized result summaries.
- Focused protocol, client, resolver, and executor tests added for command dispatch.

## Immediate Next Milestone

Harden command dispatch for operational use.

Definition of done:

- Serialize command execution per server so two dispatch requests cannot race the same target.
- Add command execution metrics for queued, dispatched, succeeded, failed, timed out, and authentication-failed commands.
- Add structured logs that identify command/server ids without logging command secrets.
- Add a guarded local smoke helper for an owned server with explicit confirmation before real RCON dispatch.
- Decide whether dispatch should stay explicit-only or gain a background worker for pending commands.

## Next Tasks

1. Add command execution metrics and logging.

2. Add per-server dispatch serialization and a narrow concurrency test.

3. Add an opt-in local RCON smoke helper for owned servers.

## v1 API Scope

Servers:

- `POST /api/servers`
- `GET /api/servers`
- `GET /api/servers/{id}`
- `PATCH /api/servers/{id}`
- `POST /api/servers/{id}/enable`
- `POST /api/servers/{id}/disable`

Credentials:

- `PUT /api/servers/{id}/credentials/rcon`
- `GET /api/servers/{id}/credentials`

Monitoring:

- `GET /api/servers/{id}/status`
- `GET /api/servers/{id}/snapshots?from=&to=`
- `GET /api/dashboard/overview`

Incidents:

- `GET /api/incidents/open`
- `GET /api/servers/{id}/incidents`
- `GET /api/incidents/{id}`

Commands:

- `POST /api/servers/{id}/commands/change-map`
- `POST /api/servers/{id}/commands/restart`
- `POST /api/servers/{id}/commands/say`
- `POST /api/servers/{id}/commands/raw`
- `GET /api/servers/{id}/commands`
- `GET /api/commands/{commandId}`
- `POST /api/commands/{commandId}/dispatch`

Health and metrics:

- `GET /health/live`
- `GET /health/ready`
- `GET /metrics`

## Initial Entities

`Server`:

- `Id`
- `Name`
- `Game`
- `Host`
- `QueryPort`
- `RconPort`
- `IsEnabled`
- `PollIntervalSeconds`
- `Notes`
- `CreatedAtUtc`

`ServerCurrentState`:

- `ServerId`
- `Status`
- `IsReachable`
- `LastCheckedAtUtc`
- `LastSuccessAtUtc`
- `LatencyMs`
- `CurrentMap`
- `Players`
- `MaxPlayers`
- `FailureReason`

`PollSnapshot`:

- `Id`
- `ServerId`
- `CheckedAtUtc`
- `IsReachable`
- `LatencyMs`
- `Map`
- `Players`
- `MaxPlayers`
- `Bots`
- `RawVersion`
- `FailureReason`

`AvailabilityIncident`:

- `Id`
- `ServerId`
- `Type`
- `OpenedAtUtc`
- `ClosedAtUtc`
- `StartReason`
- `EndReason`
- `ConsecutiveFailures`

`ServerCredential`:

- `Id`
- `ServerId`
- `Kind`
- `SecretReference`
- `CreatedAtUtc`
- `UpdatedAtUtc`

`CommandExecution`:

- `Id`
- `ServerId`
- `Type`
- `Status`
- `Payload`
- `RequestedBy`
- `RequestedAtUtc`
- `StartedAtUtc`
- `CompletedAtUtc`
- `ResultSummary`
- `FailureReason`

Future entities:

- `PlayerSnapshot`
- `AlertDelivery`
- `AuditEntry`

## Testing Plan

Start focused:

- Unit tests for A2S packet parsing with captured byte arrays.
- Unit tests for state transition rules.
- Integration tests for `POST /api/servers` and `GET /api/servers/{id}/status`.
- Testcontainers for PostgreSQL.
- Fake query client for deterministic polling tests.
- Integration test for incident opening after repeated failures.
- Integration coverage for `PATCH /api/servers/{id}`.
- Integration coverage for enable/disable behavior.
- Unit coverage for command execution and credential domain rules.
- API integration coverage for command queueing and credential metadata.
- PostgreSQL-backed integration coverage for the command and credential schema.
- Unit coverage for secret-reference resolution and GoldSrc RCON protocol/client behavior.

Later:

- Command execution metrics, serialized dispatch, and guarded live smoke automation.

## Portfolio Readiness Checklist

Before calling v1 done:

- One-command local startup.
- Clear README.
- Architecture diagram or concise text diagram.
- Working polling against at least one real server.
- Current state and snapshot history.
- Basic incident detection.
- Health checks.
- Metrics.
- A few meaningful tests.
- A short section explaining trade-offs.
