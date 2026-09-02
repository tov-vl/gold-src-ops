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
- Deployment integrations should remain provider-independent. GoldSrcOps owns
  monitoring and operator workflows across the A2S/RCON boundary; it does not
  reproduce a managed game host's billing or control-panel capabilities.

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
- Bounded dead-letter inspection and audited single-message replay with
  Operator authorization, durable idempotency, and PostgreSQL concurrency
  protection.
- A production-oriented .NET 10 container image with non-root execution,
  separate EF migrations, and isolated PostgreSQL-backed CI smoke coverage.
- Tag-gated immutable GHCR image publication with strict stable and
  release-candidate tags, build-once candidate promotion, OCI metadata,
  write-once references, digest recording, and digest-based smoke coverage.
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
- Published v2.1.0 release evidence covering bounded dead-letter
  inspection, audited single-message replay, additive replay persistence, and
  replay observability in `docs/release-notes-v2.1.md` and
  `docs/v2.1-readiness.md`.
- Published v2.2.0 release evidence covering bounded multi-datagram RCON
  responses, endpoint isolation, validated receive limits, and owned-server
  evidence in `docs/release-notes-v2.2.md` and `docs/v2.2-readiness.md`.

The original `A2S_INFO` console spike remains available as a protocol diagnostic
tool, and its live query was verified against a public GoldSrc server. See the
README for current startup commands and `docs/backlog.md` for active work.

The completed v2.1 milestone covers operator-facing dead-letter inspection and
replay in `docs/dead-letter-replay.md`. Its additive replay metadata,
append-only audit persistence, bounded Reader inspection, transactional
Operator replay, durable replay-record API, replay telemetry, sanitized
lifecycle logs, and release-gate verification are implemented and integrated
through protected `main`. Final candidate integration, signed tagging,
tag-triggered verification, and GitHub Release publication are complete.

The v2.2 slice hardens the existing GoldSrc RCON integration by
collecting bounded multi-datagram command responses under one end-to-end
deadline. The collector, receive limits, endpoint isolation, and synthetic UDP
coverage are implemented without changing the HTTP or database contracts.
Owned-server verification is complete against an isolated local ReHLDS
instance. Pull request #20 integrated the implementation through protected
`main`, and pull request #23 integrated the final release documentation.
Post-merge run #76 and signed-tag run #77 passed on release revision `9e02f07`.
Final candidate integration, signed tagging, tag-triggered verification, and
GitHub Release publication are complete; the design and residual UDP limits are
recorded in
`docs/v2.2-rcon-response-reliability.md`.

The current release is production-oriented but is not yet claimed as a
continuously operated production environment. Stable and release-candidate image
publication is implemented, including digest-preserving promotion. Signed tag
`v2.3.0-rc.1` published and verified the first immutable candidate digest,
`sha256:d21a5cb10bb8179310e660d50cb301fed277a5dc3fcbd900e77065fdcc9df458`,
without creating a stable release or mutable image alias.
The project now operates the initial controlled external ReHLDS runtime and
exports production metrics through a private OpenTelemetry Collector,
Prometheus, and Grafana path. It does not yet include a web operator experience.
That is a later delivery gap rather than a missing v2.2 release requirement.
The active v2.3 plan closes the remaining deployment and recovery gaps before
frontend or gameplay-agent work begins. Slice 2 uses
the provider-independent controlled game-server contract in
`docs/v2.3-controlled-gameserver-baseline.md`. Its bounded-trial game VDS has
passed the reviewed host foundation, controlled reboot, pinned runtime install,
and rollback-safe activation gates. The service is active while remaining
disabled across reboot. Provider game protection is configured for the reviewed
UDP endpoint; external A2S, source-address preservation, authenticated
`rcon_users`, and secret-containment checks have passed from the production
control plane. The reviewed endpoint is now registered in production; three
scheduled A2S snapshots over 120 seconds succeeded with zero failures and zero
bots. One guarded production `say` has now completed with a persisted Operator
identity, `Succeeded` terminal state, complete execution timestamps, and no
failure reason; PostgreSQL and ReHLDS journal checks independently confirmed the
single dispatch while the service remained active with zero restarts. A
controlled stop, atomic configuration restore, post-restart RCON allowlist
check, and 31 healthy scheduled A2S snapshots across 30 minutes and 2 seconds
have also passed with zero bots and no process replacement. The standalone
24-hour no-bot observation was removed from Slice 2 by an accepted scope
decision; zero-bot evidence remains part of the later 72-hour deployment soak.
Slice 3 includes the provider-independent Compose contract, same-image migration
action, and encrypted off-host PostgreSQL backup and isolated restore rehearsal.
The control-plane VPS has completed hardening, backup and recovery gates,
migrations, public TLS, external OIDC policy checks, and the digest-pinned
`v2.3.0-rc.5` runtime rollout. The recurring encrypted backup schedule and
freshness evidence are active. The private observability target rollout has
also passed health, metric-path, provisioning, network-isolation, and host
readiness checks with root-only evidence retained outside Git. The full
sequence is recorded in `docs/v2.3-production-deployment.md`.

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

- v1: released modular-monolith monitoring and administration baseline.
- v2.0 through v2.2: released transactional alert delivery, audited recovery,
  and bounded RCON response reliability.
- v2.3: provider-independent reference production deployment, real external
  ReHLDS boundary, immutable delivery, and production OTLP metrics through an
  OpenTelemetry Collector.
- A following milestone: compact Blazor Web App with a sanitized public
  dashboard and an authenticated Reader/Operator area.
- A later portfolio milestone: uptime and SLO evidence, a controlled
  failure/recovery demonstration, a short video, and a small postmortem.
- A later product experiment: versioned AMX Mod X/ReAPI gameplay events through
  a durable inbox. Sandbox entitlements may follow; real payments remain
  explicitly out of scope until then.
- Optional service extraction or a broker only if observed scaling, ownership,
  or failure-isolation pressure makes the modular monolith insufficient.

See `docs/architecture-decisions.md` for reasoning.
