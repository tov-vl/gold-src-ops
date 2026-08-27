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
- Published v1.0.0 release notes summarize delivered scope, reliability and
  security decisions, verification evidence, operational limits, and deferred
  work.
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
- The final release checklist was integrated into `main` through a signed
  linear commit, and its required `Quality Gate` passed.
- The signed annotated `v1.0.0` tag identifies the verified release revision,
  and its tag-triggered `Quality Gate` passed.
- The
  [GoldSrcOps v1.0.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v1.0.0)
  is published as the initial stable release.
- The v1.1 container baseline now uses a multi-stage .NET 10 image, runs the
  API as a non-root user on port `8080`, excludes local configuration, and
  keeps EF migration execution outside application startup.
- An isolated container smoke script verifies image contents, production
  configuration fail-fast behavior, separate EF migrations, liveness, and
  PostgreSQL-backed readiness with finally-based Docker resource cleanup.
- GitHub Actions runs the container smoke script in a dedicated job after the
  regular code quality gate succeeds.
- The active `main` ruleset requires both `Quality Gate` and `Container Smoke`
  while retaining signed commits, linear history, deletion protection, and
  force-push protection.
- The v1.1 deployment guide defines immutable image versioning, the runtime and
  configuration contract, singleton-worker topology, separate EF migrations,
  health probes, rollout order, and application/database rollback boundaries.
- OpenTelemetry SDK and instrumentation packages are aligned on stable `1.18.0`,
  and the direct Prometheus exporter is upgraded to the latest available
  `1.18.0-beta.1` with endpoint integration coverage retained.
- Architecture Decision 12 records why v1.1 keeps the authenticated direct
  exporter despite its prerelease status and when to replace it with stable
  OTLP export through an OpenTelemetry Collector.
- The v1.1 readiness matrix records the compatibility review, final local
  quality gate, production container smoke evidence, accepted deployment
  boundaries, and publication prerequisites.
- Published v1.1.0 release notes describe the focused operability delta from
  v1.0.0 without claiming an API or database-schema change.
- The final v1.1 pull request, post-merge `main` run, and signed-tag run passed
  both required GitHub Actions jobs on revision `eb0f02e`.
- The signed annotated `v1.1.0` tag identifies that verified revision, and the
  [GoldSrcOps v1.1.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v1.1.0)
  is published as the preceding stable release.
- Versioned incident-alert contracts, the EF Core outbox model, an additive
  migration, database invariants, and PostgreSQL migration coverage are added.
- Polling transactionally enqueues unavailable and recovered alerts through an
  explicit outbox writer and unit of work. Deterministic and PostgreSQL tests
  cover commit, rollback, and duplicate prevention.
- PostgreSQL atomically claims due outbox messages, conditionally completes or
  reschedules the active claim, recovers expired claims, and preserves ordering
  per incident. Concurrent PostgreSQL tests cover the claim state machine.
- The generic HTTP webhook adapter sends one POST per application attempt with
  a stable idempotency key, classifies retryable and permanent outcomes, honors
  bounded `Retry-After`, applies a request timeout, rejects implicit redirects,
  and never reads response bodies. Synthetic Kestrel tests cover the network
  boundary.
- The hosted alert dispatcher runs each attempt in its own scope, owns bounded
  exponential retry scheduling, dead-letters permanent and exhausted messages,
  recovers expired claims without exceeding the attempt limit, and deletes one
  bounded batch of expired processed rows per cleanup pass. Startup validation,
  safe structured logs, OpenTelemetry counters, duration metrics, and backlog
  gauges are covered by unit, Prometheus, and PostgreSQL tests. Delivery remains
  disabled by default until deployment configuration is supplied.
- Alert-delivery rollout, topology, secret injection, telemetry, recovery, and
  rollback are documented. The production container smoke verifies HTTPS
  configuration fail-fast, enabled-dispatcher startup, endpoint/authorization
  log safety, separate migrations, hardening, and health probes.
- The complete v2 alert-delivery capability was integrated through pull request
  #8 into protected `main` as verified squash commit `2f17aa8`. Required
  `Quality Gate` and `Container Smoke` checks passed before and after merge,
  local `main` was synchronized, and the merged feature branch was removed.
- Published v2.0.0 release notes record delivery semantics, compatibility,
  migration, deployment, verification evidence, and intentional limits.
- Final candidate pull request #10, its post-merge `main` run, and the signed-tag
  run passed `Quality Gate` and `Container Smoke` on revision `9d7176f`.
- The signed annotated `v2.0.0` tag identifies that verified revision, and the
  [GoldSrcOps v2.0.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.0.0)
  is published as the preceding stable release.
- Bounded Reader dead-letter inspection, transactional Operator replay, durable
  audit reads, idempotency, and concurrent-request protection were integrated
  through pull request #15 as verified squash commit `6a8b486`; its required
  pull-request and post-merge checks passed.
- Replay outcome telemetry now exposes only `accepted`, `idempotent`,
  `conflict`, and `invalid` labels. Source-generated lifecycle logs and focused
  unit, API, Prometheus, cancellation, and log-safety tests cover the operator
  recovery path without recording subjects, reasons, payloads, or failure
  details.
- The final local dead-letter replay gate passed audit restore, format
  verification, a zero-warning solution build, all 239 tests, a transitive
  vulnerability report with no findings, and the production container smoke
  against isolated PostgreSQL.
- Pull request #16 integrated replay observability and final operations guidance
  into protected `main` as verified squash commit `bd000b7`. Its required
  pull-request and genuine post-merge `Quality Gate` and `Container Smoke`
  checks passed, local `main` was synchronized, and the merged feature branch
  was removed.
- Published v2.1.0 release notes record the additive inspection and replay API,
  database compatibility, operator workflow, accepted boundaries, and final
  verification evidence without rewriting the published v2.0.0 history.
- Final candidate pull request #17, its post-merge `main` run, and the signed-tag
  run passed `Quality Gate` and `Container Smoke` on revision `af7c2f4`.
- The signed annotated `v2.1.0` tag identifies that verified revision, and the
  [GoldSrcOps v2.1.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.1.0)
  is published as the latest stable release.

## Completed v1.1 Milestone

GoldSrcOps v1.1.0 was published on 2026-08-23 as a focused operability release
without changing the v1 API contract or pulling the deferred v2 outbox work
forward.

Release status: implementation, local readiness evidence, required remote
checks, signed tag, and GitHub Release publication are complete.

Completed definition of done:

- A production-oriented container image packages the API with a non-root
  runtime and leaves EF migration execution as a separate deployment action.
- CI builds the image and smoke-tests it against PostgreSQL using the documented
  configuration contract.
- Deployment documentation covers image versioning, health probes,
  configuration, migrations, and rollback expectations.
- The prerelease Prometheus exporter dependency is reevaluated before v1.1 and
  either upgraded or retained with an explicit current rationale.
- The public API and v1 reliability semantics remain backward compatible.

## Completed v2.0.0 Milestone

The first v2 capability is defined by Decision 13 and
`docs/v2-alert-outbox.md`.

GoldSrcOps v2.0.0 was published on 2026-08-26 as the first supported
incident-alert delivery release.

Release status: implementation, local readiness evidence, required remote
checks, signed tag, and GitHub Release publication are complete.

Completed slices:

1. Add versioned incident-alert contracts, the EF Core outbox model and
   configuration, an additive migration, database constraints, and a
   PostgreSQL migration test.
2. Add the explicit outbox writer and unit of work, then enqueue unavailable
   and recovered events inside the existing incident transaction. Cover commit,
   rollback, and duplicate prevention through polling and PostgreSQL tests.
3. Add the PostgreSQL claim protocol, conditional completion, expiring-claim
   recovery, retry scheduling, per-incident ordering, and concurrent-dispatcher
   integration tests.
4. Add the generic HTTP webhook adapter and synthetic-server tests for
   idempotency headers, status classification, timeouts, bounded responses, and
   one request per application attempt.
5. Add the hosted dispatcher, validated configuration, OpenTelemetry metrics,
   sanitized structured logs, dead-letter behavior, and bounded processed-row
   retention.
6. Complete rollout and operations documentation, then run the full local
   quality gate and production container smoke test.

All six implementation slices and the protected-main integration workflow are
complete. Pull request #8 integrated the capability, pull request #9 recorded
its readiness, and final candidate pull request #10 integrated the release
notes. The post-merge `main` and signed-tag workflows passed on `9d7176f`, and
the stable GitHub Release is published. Evidence and accepted boundaries are
recorded in `docs/v2-readiness.md`.

Published repository: [tov-vl/gold-src-ops](https://github.com/tov-vl/gold-src-ops)

Configured GitHub description:

> Production-minded .NET 10 control plane for monitoring and administering
> GoldSrc servers through A2S and RCON.

Configured topics: `dotnet`, `aspnet-core`, `postgresql`, `opentelemetry`,
`goldsrc`, `counter-strike`, `a2s`, `rcon`, and `testcontainers`.

## Completed v2.1.0 Milestone

GoldSrcOps v2.1.0 was published on 2026-08-27 as a backward-compatible
dead-letter recovery release. Its accepted contract and operational boundaries
are documented in `docs/dead-letter-replay.md` and `docs/alert-delivery.md`.

Release status: implementation, local readiness evidence, required remote
checks, signed tag, and GitHub Release publication are complete.

Completed slices:

1. Add replay metadata, append-only audit persistence, constraints, indexes,
   and a new additive PostgreSQL migration.
2. Add bounded `Reader` inspection endpoints with cursor pagination and a
   newer-event ordering warning.
3. Add the single-message `Operator` replay endpoint with stable event identity,
   explicit idempotency, atomic audit, and concurrent-request protection.
4. Add replay outcome metrics and sanitized lifecycle logs, complete final
   operations guidance, and run the full release-gate verification.

All four implementation slices and the protected-main integration workflow are
complete. Pull request #15 integrated the audited replay capability, pull
request #16 completed observability and operations guidance, and final candidate
pull request #17 integrated the release notes. The post-merge `main` and
signed-tag workflows passed on `af7c2f4`, and the stable GitHub Release is
published. Evidence and accepted boundaries are recorded in
`docs/v2.1-readiness.md`.

A second delivery channel, broker, service extraction, bulk replay, and a
distributed polling claim remain deferred until their scaling, receiver, or
ownership requirements become concrete.

## Current API Scope

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

Alert delivery:

- `GET /api/alert-delivery/dead-letters`
- `GET /api/alert-delivery/dead-letters/{eventId}`
- `POST /api/alert-delivery/dead-letters/{eventId}/replay`
- `GET /api/alert-delivery/replays/{requestId}`

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

Alert delivery state and replay audit are intentionally Infrastructure
persistence models, not future Domain entities.

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
- API and PostgreSQL integration coverage for bounded dead-letter inspection,
  audited replay, idempotency, rollback, aggregate ordering, and concurrent
  requests.
- Unit and API integration coverage for replay outcome metrics, Prometheus
  export, HTTP-validation accounting, sanitized lifecycle logs, cancellation,
  and fault redaction.

For v1.1:

- Container-image smoke coverage for the documented deployment shape.

For v2 alert delivery:

- Unit and PostgreSQL integration coverage for transactional enqueueing,
  claiming, ordering, retry, dead-letter, stale-claim recovery, metrics, log
  safety, and bounded retention.
- Synthetic HTTP-server coverage for one POST per attempt, idempotency headers,
  status classification, timeout, `Retry-After`, redirect, and response bounds.
- Production container smoke coverage for HTTPS startup validation, enabled
  dispatcher registration, and endpoint/authorization log safety.

## v1 Portfolio Readiness

The released v1 baseline includes:

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
