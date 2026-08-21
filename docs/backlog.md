# GoldSrcOps Backlog

This backlog tracks completed milestones and the next reviewable development or
release steps.

## Current Status

Completed:

- Repository and solution initialized.
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
- Configurable poll-snapshot retention added with fail-fast bounds for retention,
  cadence, and batch size.
- A background cleanup worker deletes one oldest PostgreSQL batch per pass while
  preserving snapshots at the cutoff, current server state, and incident history.
- Retention completion, deletion, failure, and duration metrics added with unit,
  Prometheus endpoint, and PostgreSQL Testcontainers coverage.
- A concurrent `(CheckedAtUtc, Id)` index and operational retention guide added.
- The one-command Development startup was verified and repaired to restore with
  its selected SDK and forward EF application arguments correctly.
- Local Bearer issuer/audience settings moved to ignored
  `appsettings.Local.json` through a dedicated helper, leaving tracked
  configuration clean.
- The v1 readiness matrix and runtime evidence were recorded in
  `docs/v1-readiness.md`; no unresolved v1 blocker remains.
- A five-to-ten-minute presenter guide now demonstrates auth, live A2S polling,
  deterministic incident creation, durable command audit, and observability
  without sending RCON traffic to a third-party server.
- Draft v1.0.0 release notes now summarize delivered scope, reliability and
  security decisions, verification evidence, operational limits, and deferred
  work without claiming that a tag has already been published.
- Public-release hygiene removed local workspace paths and internal process
  wording, replaced the development-log README opening with a release-facing
  summary, and added a documentation map and explicit prerequisites.
- All existing commits have valid SSH signatures, and a high-signal scan of the
  complete Git history found no private-key, JWT, or common provider-token
  patterns. Tracked passwords are limited to documented local Docker defaults
  and test fixtures.
- The public repository license is MIT, with the canonical text and copyright
  notice stored in the root `LICENSE` file.
- The public GitHub repository is published at `tov-vl/gold-src-ops` with
  `main` as its default branch and the release-facing description and topics.
- Private vulnerability reporting, the dependency graph, Dependabot alerts and
  security updates, Secret Protection, and push protection are enabled.
- The active `Protect main` ruleset has no bypasses and requires signed commits,
  linear history, and the GitHub Actions `Quality Gate`; it also blocks branch
  deletion and force pushes.
- The initial publication commit was pushed and passed the GitHub Actions
  `Quality Gate`.
- Release documentation was integrated into `main` through a signed linear
  commit, and the required `Quality Gate` passed for the resulting revision.

## Immediate Next Milestone

Package the verified v1 release and portfolio narrative.

Definition of done:

- A short demo guide presents the primary A2S, incident, auth, command, and
  observability flows in a repeatable order.
- Release notes summarize the v1 scope, reliability decisions, verification,
  and intentionally deferred work.
- Repository-facing documentation tells one consistent portfolio story without
  claiming alert delivery, horizontal polling, or production identity hosting.
- Repository metadata, security settings, and the protected `main` workflow are
  configured for the public project.
- A signed `v1.0.0` tag identifies a final `main` commit that passed both the
  local and GitHub quality gates.

## Next Tasks

1. Run the complete local quality gate with Docker against the final release
   candidate commit.
2. Confirm that the GitHub Actions `Quality Gate` passed for the same SHA on
   `main`.
3. Create and push a signed `v1.0.0` tag for that commit.

Published repository: [tov-vl/gold-src-ops](https://github.com/tov-vl/gold-src-ops)

Configured GitHub description:

> Production-minded .NET 10 control plane for monitoring and administering
> GoldSrc servers through A2S and RCON.

Configured topics: `dotnet`, `aspnet-core`, `postgresql`, `opentelemetry`,
`goldsrc`, `counter-strike`, `a2s`, `rcon`, and `testcontainers`.

## Following Milestone

Design alert delivery around an outbox as the first v2 capability. A distributed
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
- Unit and PostgreSQL integration coverage for snapshot-retention cutoff,
  bounded batching, metrics, and preservation of non-snapshot monitoring data.

Later:

- End-to-end release smoke coverage for the final deployment shape.

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
