# Architecture Decisions

This document captures the durable reasoning behind the v1 architecture and its
intended evolution.

## Decision 1: Focus On GoldSrc First

Decision:

Start with GoldSrc / CS 1.6 servers instead of building a generic game-server monitoring platform from day one.

Reasoning:

- The developer has real domain experience with CS 1.6 servers and AMXX plugins.
- GoldSrc gives the project a clear and bounded first integration.
- A finished narrow system is stronger for a portfolio than a broad but shallow platform.
- Other game engines can be added later through adapters.

Implementation implication:

Introduce interfaces where useful, but do not over-generalize early.

Example future shape:

```csharp
public interface IGameServerQueryClient
{
    Task<GameServerInfo> QueryInfoAsync(GameServerEndpoint endpoint, CancellationToken cancellationToken);
}

public sealed class GoldSrcServerQueryClient : IGameServerQueryClient
{
}
```

## Decision 2: Use Modular Monolith For v1

Decision:

Use a modular monolith, not microservices, for the first production-like version.

Reasoning:

- One developer.
- One repository.
- One deployment lifecycle.
- One main bounded context.
- No proven need for independent scaling yet.
- No need to introduce distributed failure modes before the core product works.

This is the preferred interview explanation:

> I started with a modular monolith because the system had one bounded context and one deployment lifecycle at MVP stage. I kept module boundaries clear so collector or notification workflows can be extracted later if operational pressure appears.

Recommended solution shape:

```text
src/
  GoldSrcOps.Api
  GoldSrcOps.Contracts
  GoldSrcOps.Application
  GoldSrcOps.Domain
  GoldSrcOps.Infrastructure

tests/
  GoldSrcOps.UnitTests
  GoldSrcOps.IntegrationTests
  GoldSrcOps.ArchitectureTests
```

Recommended module areas:

- Servers.
- Monitoring.
- Commands.
- Incidents.
- Dashboard.
- Alerts.

## Decision 3: No Kafka, RabbitMQ, Or Event Store In v1

Decision:

Do not add a message broker at the start.

Reasoning:

- v1 can run with in-process background workers.
- Polling and incident detection do not require distributed messaging yet.
- A broker would add infrastructure and failure modes before they solve a real problem.

Preferred evolution:

- v1: direct in-process handling.
- v2: outbox table plus local outbox processor.
- v3: optional RabbitMQ if a processor is moved into a separate service.

RabbitMQ:

Reasonable later if notification delivery, command execution, or collectors become separate services.

Kafka:

Not recommended for this project stage. It is better suited to high-throughput event streams and replay-heavy analytics.

Event Store:

Not recommended. Event sourcing does not appear to be the natural source-of-truth model for this operational system.

## Decision 4: Do Not Use Orleans In v1

Decision:

Do not use Orleans in the first version.

Reasoning:

- Orleans could model each server as a grain, but it adds runtime and storage complexity.
- The project should first prove the backend domain and protocol integration.
- Orleans may distract from the portfolio story unless the project intentionally becomes an actor-model experiment.

When Orleans could be reconsidered:

- Thousands of stateful server entities.
- Complex per-server orchestration.
- High concurrency for commands against the same server.
- Strong need for serialized processing per entity.
- A deliberate goal to demonstrate actor-model architecture.

## Decision 5: Add Outbox Before Extracting Services

Decision:

If side effects become more complex, add an outbox before introducing separate services.

Target v2 flow:

```text
MonitoringHandler
  -> updates ServerCurrentState
  -> creates AvailabilityIncident
  -> appends ServerBecameUnavailable to outbox_messages

OutboxProcessor
  -> reads event
  -> invokes alert handler
  -> marks message processed
```

Reasoning:

- Improves reliability without requiring distributed infrastructure.
- Makes retries and side effects explicit.
- Creates a clean path to RabbitMQ later.

Decision 13 and `docs/v2-alert-outbox.md` refine this target into the accepted
first v2 design.

## Decision 6: First Service Extraction Candidates

If the project reaches v3, consider extracting:

- Collector Service: polls game servers and writes/publishes snapshots.
- Notification Service: handles Telegram/Discord/email delivery, retries, and deduplication.

Do not extract the core API first unless there is a clear reason.

## Decision 7: First Technical Risk Is A2S, Not CRUD

Decision:

Implement A2S spike before creating the full backend skeleton.

Reasoning:

- The riskiest part is UDP protocol behavior, timeouts, challenge responses, and binary parsing.
- CRUD and PostgreSQL are predictable.
- The spike proves the project is grounded in a real working protocol.

Current result:

The repository already contains the A2S spike under `src/GoldSrcOps.A2SSpike`.

## Decision 8: Store Credential References, Not RCON Passwords

Decision:

Persist RCON credentials as canonical `rcon-secret://<alias>` references instead of storing raw passwords or caller-selected configuration paths.

Reasoning:

- The API and read models should never echo secret values.
- Allowing arbitrary `env://` or `config://` paths would let an API caller select unrelated application secrets and send them to a configured RCON endpoint.
- A dedicated `RconSecrets:<alias>` namespace limits resolution to secrets owned by the RCON integration.
- Local development can populate that namespace through Secret Manager or environment variables.
- Production can later resolve the same aliases through a stronger secret store.
- Command execution should be auditable without mixing command history and credential material.

Implementation implication:

- The credential API accepts a validated `secretAlias`, not a secret value or configuration key.
- `ServerCredential.SecretReference` stores a canonical `rcon-secret://<alias>` value.
- Aliases use a constrained ASCII format that cannot contain configuration-section separators.
- Credential response contracts expose metadata only: id, server id, kind, configured flag, and timestamps.
- `CommandExecution` records are created as `Pending` and dispatched through `IRconCommandExecutor`.
- `GoldSrcRconCommandExecutor` resolves credentials inside Infrastructure and calls the GoldSrc RCON client over UDP.
- `ConfigurationSecretReferenceResolver` reads only `RconSecrets:<alias>`; legacy `env://`, `config://`, and `dev-secrets://` references are unsupported and must be replaced.
- Missing, unsupported, timed-out, authentication-failed, and protocol-failed dispatch paths return stable failure messages without leaking raw credential values to contracts, logs, metrics, or command history.

## Decision 9: Validate External JWT Access Tokens

Decision:

Run GoldSrcOps as an OAuth 2.0 / OpenID Connect resource server. Validate JWT
bearer access tokens issued by an external identity provider, authorize requests
through `Reader` and `Operator` policies, and use `dotnet user-jwts` only for
local development.

Reasoning:

- GoldSrcOps is a control plane that can change server configuration and send
  RCON commands, so network reachability alone is not an adequate trust boundary.
- OAuth 2.0 / OpenID Connect access tokens provide standard issuer, audience,
  signature, and expiration validation semantics and can carry application roles
  without adding user or token management to this service.
- GoldSrcOps should validate tokens, not issue production tokens or accept a
  bespoke username/password exchange.
- A custom API-key handler would introduce application-specific authentication,
  long-lived shared credentials, and weaker per-operator audit identity without
  solving a requirement unique to this project.
- `dotnet user-jwts` provides isolated local tokens without weakening the
  production authentication path.

Implementation implication:

- Add ASP.NET Core JWT bearer authentication and validate issuer, audience,
  signature, and lifetime in every non-test environment.
- Require a stable `sub` claim and use it as `CommandExecution.RequestedBy`.
- Remove `RequestedBy` from command request contracts while retaining it in
  responses and persistence; no database migration is required.
- Define `Reader` to accept `Reader` or `Operator` application roles and define
  `Operator` to accept only the `Operator` role.
- Use `Operator` as the fallback policy, apply `Reader` explicitly to reads and
  metrics, and allow anonymous access only to bounded liveness/readiness probes.
- Return `401` for failed authentication and `403` for an authenticated principal
  that does not satisfy the endpoint policy.
- Keep authentication replacement strictly inside the integration-test host;
  do not add a production runtime bypass.
- Follow the endpoint matrix and verification requirements in `docs/security.md`.

Implementation status:

Implemented. JWT bearer validation, endpoint policies, subject-derived command
audit identity, and integration-test authentication overrides are in place.

## Decision 10: Keep The Polling Worker Singleton In v1

Decision:

Run exactly one active `GoldSrcPollingBackgroundService` instance in a v1
deployment. Additional API instances must set `Polling:Enabled` to `false`.

Reasoning:

- Polling currently selects due servers from persisted current state without a
  distributed claim or lease.
- Multiple active pollers could query the same server concurrently and produce
  duplicate snapshots or competing incident transitions.
- Horizontal worker scaling is not a v1 requirement, and a singleton poller is
  the simplest deployment model that preserves current semantics.
- The command dispatcher has a separate PostgreSQL claim protocol and remains
  safe to run with multiple workers or API instances.

Implementation implication:

- Enable polling on exactly one process through `Polling:Enabled=true`.
- Disable polling on HTTP-only replicas through `Polling__Enabled=false`.
- Do not infer multi-replica polling safety from the command dispatcher's
  PostgreSQL serialization.
- Before scaling polling horizontally, add an expiring PostgreSQL claim or
  lease with conditional completion. Do not hold a database transaction open
  while waiting for an A2S UDP response.

Implementation status:

Documented as a v1 deployment constraint. Distributed polling claims are
deferred until horizontal polling becomes a real requirement.

## Decision 11: Delete Expired Snapshots In One Bounded Batch Per Pass

Decision:

Run snapshot retention as a separate background use case. Each pass deletes the
oldest snapshots strictly before the retention cutoff in one PostgreSQL
statement, limited to one configured batch.

Reasoning:

- Snapshot history grows continuously and needs an explicit storage bound.
- Loading entities through the EF Core change tracker would add avoidable memory
  and round trips for a set-based operation.
- Draining every expired row in one pass would make database work and worker
  duration unbounded after downtime or a retention-policy change.
- One batch per pass gives operators a predictable cleanup rate and keeps
  completion, failure, deletion, and duration metrics unambiguous.
- Current state and incident history have different operational value and must
  not inherit the snapshot retention policy.

Implementation implication:

- Validate retention period, cleanup interval, and batch size at startup.
- Calculate a strict `CheckedAtUtc < cutoff` boundary so a row exactly on the
  cutoff remains available.
- Order candidates by `CheckedAtUtc` and `Id`, then use EF Core
  `ExecuteDeleteAsync` for at most one batch.
- Create the `(CheckedAtUtc, Id)` index concurrently to avoid blocking snapshot
  writes while the index is built.
- Use later scheduled passes to drain a backlog; tune cadence and batch size
  from observed cleanup metrics.
- Prefer one active retention worker per deployment to avoid redundant work,
  even though deleting the same eligible row is idempotent.

Implementation status:

Implemented with unit, Prometheus endpoint, and PostgreSQL Testcontainers
coverage. Operational details are documented in `docs/snapshot-retention.md`.

## Decision 12: Keep The Direct Prometheus Exporter In v1.1

Decision:

Upgrade the OpenTelemetry SDK and instrumentations to stable `1.18.0`, upgrade
`OpenTelemetry.Exporter.Prometheus.AspNetCore` to `1.18.0-beta.1`, and retain
the authenticated `/metrics` endpoint for v1.1. Do not add an OTLP exporter or
OpenTelemetry Collector to the v1.1 deployment shape.

Decision date: 2026-08-21. Revalidated for the v1.1.0 release on 2026-08-22.

Reasoning:

- NuGet has no stable release of the direct ASP.NET Core Prometheus exporter.
  Its official documentation keeps the component prerelease because it depends
  on the experimental Prometheus/OpenMetrics compatibility specification and
  recommends considering stable OTLP export for production.
- `1.18.0-beta.1` is the latest available exporter release. Relative to the
  previously pinned `1.15.3-beta.1`, the `1.16` through `1.18` releases include
  scrape serialization, content negotiation, timeout, response-size,
  concurrency, and under-load stack-overflow fixes.
- The stable `1.18.0` breaking change affects the OTLP export request-size
  default. GoldSrcOps does not reference the OTLP exporter, and the direct
  Prometheus exporter changelog contains no `1.18.0-beta.1` breaking change.
- `/metrics` is an implemented v1 operational contract. It requires `Reader` or
  `Operator`, and API integration tests cover both output and authorization.
- Introducing a collector only to remove the prerelease package would add a
  service, configuration, rollout, and failure boundary to the focused v1.1
  operability release.

Trade-offs and safeguards:

- Pin the exact exporter version; never float to an unreviewed prerelease.
- Keep the stable SDK, hosting, ASP.NET Core instrumentation, and runtime
  instrumentation packages on the same `1.18.0` line as the exporter core
  dependency.
- Treat every exporter update as potentially breaking. Review its changelog and
  rerun the authenticated Prometheus endpoint integration tests, full quality
  gate, and production container smoke test.
- Keep `/metrics` behind the `Reader` policy and a private deployment boundary.
- Reevaluate the decision before each production-oriented release. Replace the
  direct exporter when a stable version is available, or when GoldSrcOps adopts
  an OpenTelemetry Collector for OTLP metrics as an intentional deployment
  architecture change.

Implementation status:

Implemented in v1.1.0. Package status and changes were rechecked
against NuGet and the upstream OpenTelemetry .NET release notes, exporter
documentation, and changelog on 2026-08-22.

References:

- [OpenTelemetry 1.18 release notes](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.18.0/RELEASENOTES.md)
- [NuGet package](https://www.nuget.org/packages/OpenTelemetry.Exporter.Prometheus.AspNetCore/1.18.0-beta.1)
- [Exporter documentation](https://github.com/open-telemetry/opentelemetry-dotnet/blob/coreunstable-1.18.0-beta.1/src/OpenTelemetry.Exporter.Prometheus.AspNetCore/README.md)
- [Exporter changelog](https://github.com/open-telemetry/opentelemetry-dotnet/blob/coreunstable-1.18.0-beta.1/src/OpenTelemetry.Exporter.Prometheus.AspNetCore/CHANGELOG.md)

## Decision 13: Use A Transactional PostgreSQL Outbox For Incident Alerts

Decision:

Deliver the first v2 incident alerts through a transactional PostgreSQL outbox
and one deployment-configured HTTP webhook. Commit the outbox message in the
same EF Core transaction as the incident transition, then deliver it
asynchronously with an atomic claim and expiring lease.

Decision date: 2026-08-24.

Reasoning:

- Alert delivery is the first external side effect that must survive process
  failure after an incident transition commits.
- A second commit can lose an alert between the incident commit and outbox
  insert; sending inside the polling request can instead delay polling and
  cannot make the database and remote HTTP endpoint atomic.
- PostgreSQL is already the system of record and supports atomic claims with
  `FOR UPDATE SKIP LOCKED`, so a broker would add infrastructure before the
  workload requires it.
- A stable event ID and at-least-once contract make unavoidable ambiguous HTTP
  outcomes explicit and give receivers a practical deduplication mechanism.
- An explicit writer and unit of work keep the transaction visible without
  save interceptors or reflection-based event dispatch.

Implementation implications:

- Create unavailable and recovered events only when the corresponding incident
  opens or closes; repeated poll failures do not enqueue duplicate events.
- Persist immutable, versioned JSON payloads without CLR type names or secrets.
- Enforce one event of each type per incident with a database uniqueness
  constraint.
- Claim work atomically, call the webhook outside the database transaction, and
  condition completion on the active claim token.
- Use at-least-once delivery, stable `Idempotency-Key` values, bounded retry
  with jitter, dead-letter handling, stale-claim recovery, and bounded
  processed-message retention.
- Preserve ordering per incident, not globally, while older messages are
  pending or processing.
- Keep webhook failures out of liveness and readiness; expose backlog, retry,
  dead-letter, recovery, and duration telemetry instead.
- Keep the dispatcher multi-worker and multi-instance safe. The separate
  singleton polling constraint from Decision 10 remains unchanged.

Alternatives considered:

- Send the webhook synchronously from polling. Rejected because remote latency
  and availability would enter the polling path without solving atomicity.
- Insert the alert after the incident commit. Rejected because a process crash
  can permanently lose the notification.
- Introduce RabbitMQ immediately. Deferred until independent scaling, routing,
  or additional consumers justify another operational dependency.
- Use an EF Core save interceptor for event discovery. Rejected for the first
  slice because an explicit transaction boundary is easier to trace and test.

Implementation status:

Accepted for v2 design; not yet implemented. The schema, processing protocol,
rollout, and verification slices are specified in
`docs/v2-alert-outbox.md`.
