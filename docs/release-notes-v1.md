# GoldSrcOps v1.0.0 Release Notes

Prepared: 2026-08-19. Status: release candidate. Publication tag: pending.

## Overview

GoldSrcOps v1 is a production-minded .NET backend control plane for monitoring
and administering Counter-Strike 1.6 and other GoldSrc dedicated servers. It
combines scheduled A2S polling, availability history, authenticated operations,
durable RCON command execution, and operational telemetry in a modular
monolith.

This is the first supported project release. It establishes the v1 contract;
there is no upgrade compatibility promise for earlier development snapshots.

## Included In v1

- Register, update, enable, disable, and inspect GoldSrc servers through REST.
- Poll `A2S_INFO` on a schedule with `windows-1251` text support.
- Persist current status and immutable polling snapshots in PostgreSQL.
- Open and close availability incidents after configured failure and recovery
  transitions.
- Read per-server status, snapshot history, incidents, and dashboard aggregates.
- Authenticate JWT bearer tokens and authorize `Reader` and `Operator` roles.
- Queue auditable `change-map`, `restart`, `say`, and raw RCON commands.
- Dispatch commands automatically with PostgreSQL-backed claims and per-server
  serialization.
- Store external RCON secret references instead of passwords.
- Expose liveness, database readiness, structured logs, and OpenTelemetry
  Prometheus metrics.
- Delete expired polling snapshots in bounded PostgreSQL batches while
  preserving current state and incident history.

## Architecture And Reliability

- The modular monolith keeps API, Contracts, Application, Domain, and
  Infrastructure boundaries explicit without introducing distributed-system
  overhead before it is needed.
- PostgreSQL coordinates command workers with `FOR UPDATE SKIP LOCKED`. Multiple
  dispatch workers can run concurrently, while commands for one server remain
  serialized.
- RCON execution is treated as non-idempotent. A worker interruption leaves an
  explicit audit result for later inspection instead of silently retrying an
  operation whose remote outcome may be unknown.
- Command logs contain identifiers, type, state, and duration but exclude
  payloads, secret aliases, secret references, passwords, and server responses.
- Snapshot retention deletes one oldest batch per pass and uses a concurrent
  `(CheckedAtUtc, Id)` index to bound cleanup work.
- EF Core migration history is pinned to `public`, avoiding PostgreSQL
  `$user, public` search-path ambiguity when the role and application schema are
  both named `goldsrcops`.
- Production startup fails fast when the database connection, JWT issuer, or
  JWT audience is missing. Development-only JWT settings remain in ignored
  local configuration and User Secrets.
- The authorization fallback policy requires `Operator`; only bounded health
  probes are anonymous. Command `requestedBy` identity comes from the validated
  token subject rather than request data.

## Verified Baseline

- All projects target `net10.0`; `global.json` selects SDK `10.0.203` with
  `latestFeature` roll-forward.
- Persistence uses EF Core `10.0.7` and Npgsql `10.0.1`.
- PostgreSQL 16 in Docker is the verified local and integration-test database.
- The Development startup path restores with its selected SDK, restores local
  tools, applies migrations, and can be repeated against an up-to-date database.
- A live `A2S_INFO` request to `server.csomod.com:27015` reached `Online` during
  the readiness run and persisted a snapshot.
- An authenticated PostgreSQL-backed smoke flow covered server administration,
  dashboard and incident reads, command audit identity, safe pre-RCON failure,
  health, OpenAPI access, and metrics.
- Production startup without `ConnectionStrings__GoldSrcOps` was verified to
  fail fast.

The 2026-08-19 quality gate completed with:

- `141/141` tests passed, including unit, API integration, deterministic
  polling, protocol, synthetic UDP, and PostgreSQL Testcontainers coverage;
- zero build warnings;
- clean `dotnet format --verify-no-changes` output;
- no known vulnerable direct or transitive NuGet packages;
- successful repeat application of EF Core migrations.

Detailed evidence is recorded in
[docs/v1-readiness.md](v1-readiness.md).

## Running The Release

For local Development, create a short-lived token and start the stack:

```powershell
.\tools\dev\new-local-jwt.ps1
.\tools\dev\start-local.ps1
```

The local script starts PostgreSQL, applies migrations, and runs the API on
`http://localhost:5142`. Production deployments must supply their own database,
external identity-provider, reverse-proxy, and secret-store configuration.

The API does not apply migrations during ordinary startup. Run the EF migration
step as a separate deployment action before starting a new application version.

Use [docs/demo.md](demo.md) for a five-to-ten-minute walkthrough and
[docs/smoke-test.md](smoke-test.md) for the complete local verification path.

## Intentional Limits

- v1 runs one active polling worker because polling does not yet use a
  distributed lease. HTTP-only replicas must disable polling.
- Snapshot retention should be enabled on one worker to avoid redundant cleanup
  contention.
- Real RCON smoke is restricted to a server the operator owns. After a timeout,
  inspect the persisted command before retrying.
- v1 provides incidents, not alert delivery. Notifications and an outbox remain
  candidates for v2.
- A frontend, first-party identity provider, Kubernetes packaging,
  microservices, Kafka, event sourcing, and Orleans are outside the v1 scope.
- Distributed tracing and a packaged Grafana dashboard are deferred; v1 exposes
  logs, health checks, application/runtime metrics, and Prometheus output.
- `OpenTelemetry.Exporter.Prometheus.AspNetCore` is currently referenced as
  `1.15.3-beta.1`. Its endpoint is integration-tested, but the dependency should
  be reevaluated for a later production-oriented release.

## Release References

- Architecture: [docs/architecture.md](architecture.md)
- Architecture decisions: [docs/architecture-decisions.md](architecture-decisions.md)
- Security model: [docs/security.md](security.md)
- RCON operations: [docs/rcon.md](rcon.md)
- Snapshot retention: [docs/snapshot-retention.md](snapshot-retention.md)
- Demo: [docs/demo.md](demo.md)
- Full smoke test: [docs/smoke-test.md](smoke-test.md)
- Readiness evidence: [docs/v1-readiness.md](v1-readiness.md)

## Publication Status

The release tag has not been created yet. After the repository presentation is
reviewed and a publication point is selected, rerun the quality gate against
that exact commit and create a signed `v1.0.0` tag.
