# GoldSrcOps Project Brief

## One-line Summary

GoldSrcOps is a production-minded .NET backend control plane for monitoring and administering Counter-Strike 1.6 / GoldSrc dedicated servers.

## Why This Project Exists

The project is a portfolio-oriented pet project for a .NET backend developer with prior hands-on experience running CS 1.6 servers and writing AMXX plugins for Jail Break and Deathrun servers.

The goal is not to build "just an admin panel for an old game".
The goal is to build a focused backend/operations system that demonstrates practical backend engineering:

- Network protocol integration.
- Background processing.
- State tracking.
- Incident detection.
- Admin command execution.
- Auditability.
- Observability.
- Reliability trade-offs.

## Positioning

Start narrow and do it well:

- v1 focuses on GoldSrc / CS 1.6 servers.
- Future versions may support other game server engines through adapters.

Preferred framing for portfolio and interviews:

> GoldSrcOps is a backend control plane for GoldSrc dedicated servers. It polls game servers via A2S, executes operator actions through RCON, stores availability history, and exposes health, metrics, and incidents for operational visibility.

Avoid positioning it as:

- A generic monitoring platform for every game.
- A simple CS 1.6 website.
- A microservices demo.

## Current Repository State

Current implementation:

- .NET 10 modular monolith with API, Contracts, Application, Domain, and
  Infrastructure projects.
- PostgreSQL persistence through EF Core migrations.
- Scheduled A2S polling with current state, snapshot history, and availability
  incident transitions.
- Authenticated server administration through JWT bearer `Reader` and
  `Operator` policies.
- Auditable RCON command queue with PostgreSQL-backed claiming, per-server
  serialization, interrupted-command recovery, and external secret references.
- Health checks and Prometheus metrics through OpenTelemetry.
- Bounded PostgreSQL retention for historical poll snapshots with validated
  configuration and operational metrics.
- Reliable incident-alert delivery through a transactional PostgreSQL outbox,
  an at-least-once HTTPS webhook dispatcher, bounded retries, dead letters,
  processed-row retention, and OpenTelemetry backlog metrics.
- A production-oriented .NET 10 container image with non-root execution,
  separate EF migrations, and isolated PostgreSQL-backed CI smoke coverage.
- Unit, API integration, deterministic polling, protocol, and PostgreSQL
  Testcontainers coverage.
- GitHub Actions quality gate for formatting, build, tests, and NuGet audit.
- A completed v1 readiness review with startup, migration, live polling,
  authenticated API, safe command, and metrics evidence in
  `docs/v1-readiness.md`.
- Published v1.1.0 release evidence covering the production container,
  deployment contract, required container smoke gate, compatibility, and
  current OpenTelemetry decision in `docs/v1.1-readiness.md`.
- Published v2.0.0 release evidence covering reliable incident-alert delivery
  through a transactional PostgreSQL outbox and a generic HTTP webhook,
  documented in `docs/release-notes-v2.md`, `docs/v2-alert-outbox.md`, and
  `docs/alert-delivery.md`, with evidence in `docs/v2-readiness.md`.

The original `A2S_INFO` console spike remains available as a protocol diagnostic
tool, and its live query was verified against a public GoldSrc server. See the
README for current startup commands and `docs/backlog.md` for active work.

The accepted next-milestone design covers operator-facing dead-letter
inspection and replay in `docs/dead-letter-replay.md`. Its additive replay
metadata, append-only audit persistence, and bounded Reader inspection API are
implemented; the Operator replay API remains planned.

## MVP Goal

Build a modular ASP.NET Core backend that can:

- Register GoldSrc servers.
- Poll their status via A2S on a schedule.
- Store current server state.
- Store historical polling snapshots.
- Detect offline incidents.
- Expose status and history through REST endpoints.
- Authenticate API callers and authorize reader and operator capabilities.
- Execute auditable RCON commands as authenticated operators.
- Expose health checks and metrics.

## Non-goals For v1

- Full frontend UI.
- First-party identity provider or user-management UI.
- Kubernetes.
- Microservices.
- Kafka.
- Event sourcing.
- Orleans.
- Generic support for all game engines.
- Complex multi-tenant SaaS model.

## Recommended Tech Stack

- .NET 10 LTS for the current local setup.
- ASP.NET Core Web API.
- JWT bearer validation against an external OAuth 2.0 / OpenID Connect provider,
  with `dotnet user-jwts` for local development only.
- PostgreSQL.
- EF Core for write model and persistence.
- Dapper may be introduced later for read-heavy dashboard queries.
- BackgroundService for polling.
- OpenTelemetry for traces/metrics.
- Prometheus and Grafana for observability.
- Docker Compose for local development.
- xUnit for tests.

## Future Shape

The project should evolve in stages:

- v1: Modular monolith.
- v2: Modular monolith with outbox and async side effects.
- v3: Optional extraction of collector or notification service if real complexity appears.

See `docs/architecture-decisions.md` for reasoning.
