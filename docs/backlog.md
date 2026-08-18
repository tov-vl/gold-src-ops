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
- Pending commands are executed by a background dispatcher that transitions them through `Running` into `Succeeded` or `Failed`.
- Deterministic tests cover successful fake dispatch, executor failure, timeout, missing RCON port, lost completion claims, and PostgreSQL status persistence.
- RCON credentials now use validated aliases stored as canonical `rcon-secret://<alias>` references.
- Secret resolution is restricted to the dedicated `RconSecrets:<alias>` namespace; arbitrary environment and configuration keys are rejected.
- Live GoldSrc RCON client added behind `IRconCommandExecutor` with challenge/command handling, timeout mapping, authentication failure handling, and sanitized result summaries.
- Focused protocol, client, resolver, and executor tests added for command dispatch.
- Command execution metrics added for queued, dispatched, completed, recovered, succeeded, failed, timed-out, and authentication-failed command dispatch paths.
- Authentication, authorization, endpoint policy, and audit-identity model documented in `docs/security.md` and Architecture Decision 9.
- JWT bearer validation and `Reader`/`Operator` policies applied to API, metrics, OpenAPI, and anonymous health probes.
- Command request contracts no longer accept `RequestedBy`; audit identity is derived from the authenticated token subject.
- Unit and API integration tests cover subject validation, the endpoint policy matrix, and requester spoofing protection.
- PostgreSQL atomically claims pending commands, serializes execution per server across workers, and conditionally persists completion for the active claim.
- Interrupted `Running` commands are recovered as `Failed` without automatic RCON retry, and PostgreSQL integration tests cover concurrent claims and recovery.
- Polling metadata and failure reasons are bounded by domain invariants that match the EF Core column limits.
- The v1 singleton-poller deployment constraint and the trigger for distributed polling leases are documented.
- Structured RCON lifecycle logs identify command and server ids, command type,
  status, result, and duration without payload or credential material.
- Guarded local RCON smoke helper added with owned-server acknowledgement,
  authenticated preflight, `-WhatIf`, exact server-id confirmation, and a
  generated `say` command only.

## Immediate Next Milestone

Add configurable snapshot retention with bounded cleanup batches and
operational metrics.

Definition of done:

- Retention and cleanup cadence are validated configuration options.
- Old snapshots are deleted in bounded batches without affecting current state
  or incident history.
- Cleanup runs expose completion, deletion, failure, and duration signals.
- Unit and PostgreSQL-backed integration tests cover cutoff and batching rules.

## Next Tasks

1. Define snapshot-retention options and the application cleanup boundary.
2. Add bounded PostgreSQL deletion and a background cleanup worker.
3. Add cleanup metrics, tests, and operational documentation.

## Following Milestone

Review v1 portfolio readiness after retention is implemented. A distributed
polling claim or lease remains deferred until multiple active poller instances
are required.

## v1 API Scope

Access policies for these endpoints are implemented as defined in
`docs/security.md`.

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
- `SecretReference` (canonical `rcon-secret://<alias>` value)
- `CreatedAtUtc`
- `UpdatedAtUtc`

`CommandExecution`:

- `Id`
- `ServerId`
- `Type`
- `Status`
- `Payload`
- `RequestedBy` (derived from the authenticated token subject; never supplied by the command request)
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
- Unit and API integration coverage for command execution metrics.
- Unit and PostgreSQL integration coverage for background dispatch, atomic per-server claiming, and interrupted-command recovery.
- API integration coverage for anonymous, Reader, and Operator access across the endpoint policy matrix.
- API integration coverage proving command requester identity comes from the authenticated token subject.

Later:

- Snapshot-retention cleanup, metrics, and PostgreSQL batching coverage.

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
