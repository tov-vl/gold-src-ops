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

Implemented across the six reviewable v2 slices. The schema, transactional
writer, PostgreSQL claim protocol, HTTP adapter, hosted dispatcher, retry and
dead-letter policy, telemetry, retention, rollout, operations, and verification
contract are documented in `docs/v2-alert-outbox.md` and
`docs/alert-delivery.md`.

## Decision 14: Replay Dead Letters Through An Audited State Transition

Decision:

Provide operator-facing dead-letter inspection and single-message replay inside
the modular monolith. Replay the existing outbox row through an atomic
`DeadLetter` to `Pending` transition, preserving the immutable event identity
and payload. Persist an append-only audit record in the same PostgreSQL
transaction and use a client-supplied UUID idempotency key for ambiguous HTTP
retries.

Decision date: 2026-08-26.

Reasoning:

- A dead letter is intentionally terminal for automatic dispatch, but it can
  leave a delivery gap that requires an explicit operational decision.
- Routine SQL recovery bypasses application authorization, validation,
  idempotency, concurrency protection, and durable audit.
- Creating a replacement event would change the idempotency key and represent
  one incident transition as multiple events.
- Resetting the bounded attempt count is required for a new delivery cycle, but
  the previous count, failure, and dead-letter time must be retained before the
  reset.
- PostgreSQL already owns outbox state and multi-instance claims, so the replay
  mutation belongs in the same persistence boundary rather than in a new
  service or broker.

Implementation implications:

- `Reader` can list and inspect dead letters; only `Operator` can request replay.
- `RequestedBy` comes from the validated JWT `sub` claim and cannot be supplied
  by the request body.
- Each replay requires a UUID `Idempotency-Key` and a bounded operator reason.
- Repeated identical requests return the stored accepted result; key reuse for
  different intent fails with `409 Conflict`.
- A conditional state transition and database uniqueness constraints ensure
  that only one of several concurrent requests requeues the message.
- The event ID, contract version, aggregate identity, occurrence time, and JSON
  payload remain unchanged. Only delivery state and the new retry budget change.
- Replay audit survives normal processed-outbox cleanup and contains metadata,
  not payloads or secrets.
- Operators are warned when a newer event exists for the same aggregate because
  dead-letter replay cannot restore arrival order at a remote receiver.
- Replay locks the source availability incident to serialize with creation of a
  concurrent recovered event.
- Replay locks newer active aggregate rows during the transition and refuses to
  race a newer event that is already `Processing`.
- Metrics and logs use bounded outcomes and identifiers; the persisted audit
  record is the source for principal and reason.

Alternatives considered:

- Continue with manual SQL. Retained only as the temporary recovery path until
  the API exists; rejected as the normal operating model.
- Insert a new outbox event. Rejected because it weakens receiver deduplication
  and changes event identity.
- Replay every dead letter automatically. Rejected because permanent failures
  can create an unbounded retry loop and repeat external side effects.
- Start with bulk replay. Deferred until single-message safety is proven and
  operational volume demonstrates the need.
- Add a second delivery channel first. Deferred until concrete routing and
  receiver requirements outweigh recovery through the existing channel.

Implementation status:

Implemented and released in v2.1.0. Replay metadata, append-only audit
persistence, additive migrations, bounded Reader inspection, Operator replay,
replay-record reads, PostgreSQL concurrency verification, replay-specific
telemetry, sanitized lifecycle logs, and release-gate verification are in
place. The final contract and operational evidence are recorded in
`docs/dead-letter-replay.md` and `docs/v2.1-readiness.md`.

## Decision 15: Complete Legacy RCON Responses With A Bounded Quiet Drain

Decision:

Collect one or more ordinary GoldSrc `A2A_PRINT` command-response datagrams on a
UDP socket connected to the resolved server endpoint. Require the first response
inside one end-to-end deadline, then infer completion after a bounded quiet
interval. Reject known partial responses when the deadline, datagram ceiling, or
aggregate wire-byte ceiling is reached.

Decision date: 2026-08-28.

Reasoning:

- ReHLDS can flush redirected console output through several ordinary response
  datagrams without split-packet metadata or an explicit completion marker.
- Returning after one datagram can persist an incomplete response as success.
- A connected UDP socket discards datagrams from unrelated addresses or ports.
- One deadline covers DNS resolution, challenge, command send, and response
  collection without adding unsafe automatic command retry.
- Independent network ceilings bound work before the smaller sanitized result
  summary is persisted.

Alternatives considered:

- Reuse the A2S split-packet parser. Rejected because RCON response chunks have
  no indexed split envelope.
- Return immediately after the first datagram. Rejected because it preserves the
  known partial-success path.
- Wait only for the overall timeout. Rejected because every successful command
  would consume the full timeout.
- Retry after timeout. Rejected because an RCON command may already have run and
  has no idempotency key.

Implementation status:

The collector, endpoint isolation, validated defaults, protocol parsing, and
synthetic UDP coverage are implemented for v2.2.0. Timing and framing were
verified against an isolated local ReHLDS instance, and pull request #20
integrated the implementation through protected `main` with successful
post-merge checks. Final release-documentation pull request #23, post-merge run
#76, and signed-tag run #77 passed on release revision `9e02f07`; signed tagging
and GitHub Release publication are complete. Detailed bounds and residual UDP
limitations are recorded in `docs/v2.2-rcon-response-reliability.md`,
`docs/v2.2-readiness.md`, and `docs/rcon.md`.

## Decision 16: Adopt A Provider-Independent Reference Production Topology

Decision:

Operate the first persistent GoldSrcOps environment across two independent
boundaries: one controlled Counter-Strike 1.6 server running ReHLDS and
ReGameDLL_CS, and one single-node VPS control plane. The VPS hosts the immutable
GoldSrcOps application image, PostgreSQL, a TLS reverse proxy, and the
observability components defined by Decision 17. Production identity remains
external. Docker Compose is the reference orchestration mechanism for this
single-host baseline.

The game-server host may be managed or self-operated, but application behavior
must depend only on documented A2S and RCON endpoints. Provider billing,
provisioning, restart, file-management, and panel APIs remain outside the
GoldSrcOps core contract.

Decision date: 2026-08-28.

Reasoning:

- Running the game server and control plane on separate hosts exercises the
  actual DNS, UDP, firewall, latency, timeout, and partial-failure boundaries
  that loopback and CI cannot reproduce.
- A single VPS is affordable and operationally understandable for the first
  portfolio deployment. It demonstrates rollout, migration, backup,
  observability, and recovery without claiming high availability.
- The modular monolith still has one deployment lifecycle and no observed load
  that justifies Kubernetes, service extraction, or a broker.
- A provider-independent protocol boundary keeps GoldSrcOps positioned as a
  fleet control plane rather than a replacement for one hosting company's
  panel.
- A real environment should exist before the public dashboard and Operator UI
  so those workflows are shaped by observed incidents and operations data.

Trust and operational implications:

- Terminate public HTTPS at the reverse proxy. The API container, PostgreSQL,
  Collector receivers, Prometheus, and administrative Grafana endpoints stay on
  private container or host networks unless a later decision exposes them.
- Use an external OAuth 2.0 or OpenID Connect provider. The deployment must not
  introduce a production token-issuing shortcut into GoldSrcOps.
- Prefer a private tunnel between the control plane and game server for RCON.
  If a managed host cannot provide one, restrict RCON to the VPS source address
  and document the residual lack of transport confidentiality before accepting
  that target. A2S may remain publicly queryable.
- Deploy application images by immutable registry digest and retain the previous
  known-good digest for rollback. Serialize EF Core migrations before rollout.
- Keep exactly one polling worker and one snapshot-retention worker. Existing
  PostgreSQL claim protocols continue to protect command and alert dispatch.
- Treat the VPS, its local PostgreSQL volume, and its observability data as one
  failure domain. Store encrypted database backups outside that host and rehearse
  restoration before calling the milestone complete.
- Establish ReHLDS and ReGameDLL_CS baseline behavior before adding YaPB. Bot
  sessions are synthetic load and must remain distinguishable from real-player
  usage in evidence and later UI projections.

Alternatives considered:

- Run the game server and GoldSrcOps on one host. Rejected for the reference
  environment because it hides the external network and ownership boundary.
- Adopt Kubernetes immediately. Rejected because a single application instance
  and a few supporting containers do not justify its operational cost.
- Couple the application to a managed-hosting API. Deferred until more than one
  provider and a concrete lifecycle workflow justify a separate adapter.
- Require a managed PostgreSQL service. Kept compatible but not required for the
  first baseline; off-host backup and restore evidence are mandatory either way.

Implementation status:

Implemented and exercised for v2.3 across separate control-plane and game-server
hosts. Public TLS and external identity, encrypted off-host recovery, private
observability, external A2S and guarded RCON, a recovery exercise, and the
bounded release soak all passed their documented gates. This remains a
single-node reference topology and does not claim high availability. The
delivery and evidence sequence is recorded in
`docs/v2.3-production-deployment.md`.

## Decision 17: Export Production Metrics Through OTLP And A Collector

Decision:

Add stable OTLP metric export from GoldSrcOps to a private OpenTelemetry
Collector and make that pipeline the production-default v2.3 path. The
Collector acts as the local telemetry gateway and exposes a private Prometheus
target for the reference Prometheus and Grafana stack.

Preserve the authenticated application `/metrics` endpoint throughout the v2
compatibility window. It remains available for local development, existing
integrations, and rollback, but it is not the primary production collection
path. Removing the direct prerelease exporter and endpoint requires a separate
compatibility decision, normally for a major release.

Decision date: 2026-08-28.

Reasoning:

- Decision 12 explicitly identifies an intentional Collector deployment as the
  trigger for moving the production path away from the prerelease direct
  ASP.NET Core Prometheus exporter.
- OTLP keeps application instrumentation independent from the selected metrics
  backend and lets the Collector own routing, batching, filtering, and backend
  adaptation.
- A private push path avoids giving Prometheus a bearer token for the
  application endpoint and removes `/metrics` from the production scrape path.
- Preserving `/metrics` avoids an unnecessary v2 operational-contract break and
  provides a known rollback path while the new pipeline gains evidence.
- GoldSrcOps currently instruments metrics only. Adding traces or exporting
  application logs in the first deployment slice would enlarge scope without a
  demonstrated diagnostic requirement.

Implementation implications:

- Add `OpenTelemetry.Exporter.OpenTelemetryProtocol` on the same reviewed SDK
  version line and support the standard OTLP endpoint and protocol settings.
- Production startup enables OTLP explicitly. Development and tests must remain
  deterministic when no Collector is configured.
- Bind OTLP receivers, the Collector's Prometheus exporter endpoint, and
  Collector administration endpoints to private networks. Do not publish ports
  `4317`, `4318`, or the Prometheus scrape target to the Internet.
- Preserve existing instrument names, units, bounded attributes, and dashboard
  semantics. Add integration coverage that observes representative GoldSrcOps
  metrics after the Collector boundary.
- Pin the Collector distribution and every observability image by reviewed
  version or digest. Validate configuration before rollout and expose Collector
  health and internal telemetry to the private operations stack.
- Collector or Prometheus failure must not make the GoldSrcOps API unready or
  stop A2S polling, RCON dispatch, alert persistence, or replay. Telemetry loss
  is an operational incident, not an application transaction failure.
- Document the period during which direct and OTLP readers coexist. Do not infer
  duplicate business events by adding the measurements from both paths.

Alternatives considered:

- Keep direct Prometheus scraping as the only production path. Rejected because
  it preserves the prerelease component as the production boundary and couples
  application authorization to metrics collection.
- Remove `/metrics` immediately. Rejected because it breaks an authenticated and
  integration-tested v2 operational contract without necessity.
- Export directly from the application to a hosted observability vendor.
  Rejected for the reference environment because it introduces provider lock-in
  before a backend requirement exists.
- Add traces and logs in the same slice. Deferred until production evidence
  identifies concrete investigations that metrics and structured application
  logs cannot answer.

Implementation status:

Implemented as a repository deployment contract for v2.3. The API includes the
stable OTLP package, validated opt-in configuration, and direct-exporter
compatibility. Digest-pinned Collector, Prometheus, and Grafana services use a
private internal network, tracked provisioning, bounded persistent storage, and
no host-published ports. Container smoke observes application and ASP.NET Core
metrics after the Collector boundary and verifies that Collector loss does not
change API readiness. Target-environment rollout, recovery exercise, and
24-hour release-soak evidence are complete for the v2.3 reference deployment.

References:

- [OpenTelemetry Collector overview](https://opentelemetry.io/docs/collector/)
- [OpenTelemetry Collector deployment patterns](https://opentelemetry.io/docs/collector/deploy/)
- [OTLP exporter configuration](https://opentelemetry.io/docs/languages/sdk-configuration/otlp-exporter/)
- [OpenTelemetry .NET Prometheus exporter status](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.Prometheus.AspNetCore/README.md)

## Decision 18: Use Encrypted Logical Backups With Verified Restore Rehearsals

Decision:

Use PostgreSQL custom-format logical dumps streamed directly into a
client-side encrypted restic repository outside the control-plane host. The
first production backend is S3-compatible object storage over HTTPS. Do not
write a plaintext dump to host storage.

A snapshot starts as pending and becomes eligible for restore only after both
`pg_dump` and restic return zero and restic validates repository structure.
Repository data checks and a network-isolated restore rehearsal are separate
mandatory operations. The rehearsal restores through `pg_restore`, applies the
migration bundle from the exact configured API image, and verifies migration
history and required application tables before deleting its disposable volumes.

Decision date: 2026-08-29.

Reasoning:

- The first deployment has one small PostgreSQL database and no demonstrated
  point-in-time recovery requirement. A portable logical backup is easier to
  inspect and rehearse than a provider-specific physical snapshot.
- Streaming keeps plaintext database contents off the host filesystem while
  restic provides client-side encryption, integrity metadata, and off-host
  backend support.
- Checking both sides of the stdin pipeline avoids accepting a restic snapshot
  when `pg_dump` failed or produced an incomplete stream.
- A repository check alone does not prove application recovery. Restoring into
  disposable PostgreSQL and running the same-image migration bundle exercises
  the database, schema, and deployable artifact together.
- One exclusive host lock serializes backup, repository checks, retention, and
  rehearsals; restic's repository lock protects the shared backend
  independently.
- Sanitized evidence can record the exact snapshot and image digests without
  exposing database or object-storage credentials.

Security and operational implications:

- Pin PostgreSQL, restic, and API images by reviewed SHA-256 digest in the
  target environment.
- Keep the restic password and repository-scoped S3 credential in separate
  root-owned files outside Git. Retain the encryption password in an independent
  recovery escrow.
- Treat root and Docker-daemon administration as privileged access to mounted
  secrets and backend environment values.
- Run a backup immediately before migration, scheduled backups at least daily
  for the initial target, sampled data checks routinely, and a full data check
  plus restore rehearsal before the first rollout.
- Record measured backup and recovery durations before adopting a recovery-time
  objective. Daily scheduling initially implies up to approximately 24 hours of
  schedule-derived data loss.
- Apply expiration only to the exact backup host and recoverable tag. Keep the
  latest 3, 14 daily, 8 weekly, and 12 monthly snapshots.
- Require a fresh non-destructive retention preview before enabling automated
  `forget --prune`. Publish a completed-cycle marker only after backup, sampled
  data verification, pruning, and a final repository check all succeed.

Retention policy amendment date: 2026-08-31.

Alternatives considered:

- Store dumps on the control-plane VPS. Rejected because the database and its
  backup would share one failure domain.
- Rely only on provider volume snapshots. Rejected as the baseline because
  portability, application-level restore verification, and provider independence
  would be weaker. Provider snapshots may be an additional layer.
- Add continuous WAL archiving and point-in-time recovery immediately. Deferred
  until the measured write rate, recovery objectives, or incident evidence
  justify the additional lifecycle and restoration complexity.
- Use managed PostgreSQL backups only. Kept compatible, but they do not replace
  target evidence that the GoldSrcOps schema and application artifact can be
  restored together.
- Automatically restore after a failed rollout. Rejected because restoration
  overwrites database state and requires an explicit downtime and data-loss
  decision.

Implementation status:

Implemented for v2.3 in `ops/production/postgres-backup.ps1`,
`ops/production/postgres-backup-status.ps1`, the guarded systemd installer,
`ops/production/postgres-restore-rehearsal.ps1`, and the container smoke flow.
Local evidence covers encrypted backup, retention and pruning, freshness
validation, full repository data check, eight migrations, required tables, and
a restored control record. The private remote repository is initialized and
independently escrowed; its full data check, first live backup, and isolated live
restore rehearsal have passed. The target timer and its first completed cycle
also passed for v2.3. Backup and restore durations remain evidence only; no
recovery-time objective is active.

References:

- [restic repository preparation](https://restic.readthedocs.io/en/stable/030_preparing_a_new_repo.html)
- [restic backup from stdin](https://restic.readthedocs.io/en/stable/040_backup.html#reading-data-from-stdin)
- [restic integrity checks](https://restic.readthedocs.io/en/stable/045_working_with_repos.html#checking-integrity-and-consistency)
- [restic dump](https://restic.readthedocs.io/en/stable/050_restore.html#printing-files-to-stdout)
- [restic snapshot retention and pruning](https://restic.readthedocs.io/en/stable/060_forget.html)

## Decision 19: Measure Public Availability From An Independent External Probe

Decision:

Use one designated managed black-box probe outside both GoldSrcOps host failure
domains as the authoritative source for the proposed `API-01` availability SLI.
It sends an unauthenticated HTTPS `GET` to the public `/health/ready` endpoint
once per UTC minute and requires successful DNS resolution, trusted
hostname-valid TLS, no redirect, and HTTP `200` within 10 seconds.

Generate the SLI denominator from expected minute slots after an explicit
activation timestamp, not from observed result rows. Missing slots, provider
execution failures, planned maintenance, timeouts, redirects, TLS failures, and
non-`200` responses are bad. Retries and additional probe locations are
diagnostic and cannot replace or outvote the canonical primary result.

Persist raw timestamped results and monitor revisions outside the production
control plane for at least one complete 30-day window plus review margin. Keep
provider secrets, account identifiers, and raw evidence outside Git; only the
provider-independent contract, evaluator, and sanitized aggregates belong in
the repository.

Decision date: 2026-09-04.

Reasoning:

- Public readiness traverses DNS, the Internet path, Caddy, the API, and
  PostgreSQL, which is closer to the minimum useful client experience than
  host-local liveness or Collector scrape health.
- A probe on the control-plane VPS shares the failure domain it is supposed to
  measure and cannot establish external reachability.
- One primary location gives every minute a deterministic outcome. Multiple
  equal-vote locations would require a separate quorum or aggregation policy.
- An expected-slot denominator prevents missing provider data from improving
  the reported ratio.
- Managed synthetic monitoring minimizes new operational software while the
  provider-independent export preserves an exit path.
- Alert thresholds and the SLO answer different questions: notification may
  require consecutive failures, while every bad minute still consumes budget.

Alternatives considered:

- Use private Prometheus or OpenTelemetry data as public availability. Rejected
  because those signals do not prove DNS, TLS, or the external client path.
- Run Prometheus blackbox exporter beside the API. Rejected as the primary
  source because it shares the control-plane failure domain; it remains a valid
  diagnostic pattern from an independent host later.
- Use scheduled GitHub Actions. Rejected because their minimum interval is five
  minutes and scheduled runs may be delayed or dropped, so they cannot define a
  complete one-minute population.
- Build a custom .NET probe immediately. Deferred because it adds another
  service to operate without differentiating the GoldSrcOps control plane.
- Combine several regions into the headline SLI. Deferred until observed need
  justifies a reviewed aggregation policy.

Implementation status:

Design accepted for v2.4. Decision 20 conditionally selects a preferred shadow
provider, and three non-authoritative checks were configured on 2026-09-04. The
normalized exporter, create-only JSONL format, expected-slot evaluator, and
contract fixtures are implemented. No official activation timestamp exists,
and `API-01` remains inactive. Live export proof, independent retention,
provider failure-detail and alert tests, a 24-hour shadow validation, and one
complete prospective 30-day window remain required. The full contract is in
`docs/v2.4-external-availability-monitoring.md`.

References:

- [Google SRE Workbook: Implementing SLOs](https://sre.google/workbook/implementing-slos/)
- [Google SRE Workbook: Alerting on SLOs](https://sre.google/workbook/alerting-on-slos/)
- [Prometheus multi-target exporter pattern](https://prometheus.io/docs/guides/multi-target-exporter/)
- [Prometheus blackbox exporter configuration](https://github.com/prometheus/blackbox_exporter/blob/master/CONFIGURATION.md)
- [GitHub Actions scheduled workflow limits](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule)

## Decision 20: Prefer Grafana Cloud With An Independent Evidence Archive

Decision:

Use Grafana Cloud Synthetic Monitoring as the preferred provider for the v2.4
`API-01` shadow rollout. Configure one Frankfurt public probe as the canonical
one-minute `/health/ready` source, with same-location liveness and a five-minute
Stockholm readiness probe for diagnosis only.

Treat this as a conditional selection rather than SLO activation. Before an
official window begins, prove that the configured HTTP check preserves the
timeout, redirect, TLS, and exact-status contract; normalize timestamped
underlying metrics and logs; and copy recoverable evidence to a private
owner-controlled Backblaze B2 namespace for at least 45 days. Generate the
denominator from expected UTC slots rather than Grafana's displayed uptime.

Decision date: 2026-09-04.

Reasoning:

- HTTP checks support the required minute schedule, ten-second timeout,
  redirect control, certificate validation, and expected status.
- Public EMEA probes are outside both GoldSrcOps hosting failure domains.
- Prometheus metrics, probe logs, `config_version`, the Metrics API, and
  API/Terraform configuration provide the best available input for a
  reproducible provider-independent evaluator.
- The saved readiness, liveness, and secondary diagnostic checks consume an
  estimated 98,208 of the current 100,000 free API executions in a 31-day
  month, leaving only 1,792 executions of margin.
- Existing Grafana uptime aggregation is not canonical because reported-sample
  and `max_over_time` behavior does not enforce the GoldSrcOps missing-slot
  denominator.
- Free retention is 14 days, while Pro log retention is 30 days. Independent
  export is therefore required even though Pro metrics retention is longer.

Alternatives considered:

- Checkly. Not selected because its free plan cannot run every minute and its
  self-service plans retain raw results for only 7 or 30 days; the 180-day tier
  requires an enterprise agreement.
- Better Stack Uptime. Not selected because the documented response-time API is
  limited to 24 hours and a complete canonical per-attempt failure export was
  not established from the published API.
- UptimeRobot. Rejected because one-minute checking requires a paid plan and
  checks are progressively spaced out during an extended outage, violating the
  fixed expected population.
- An independent self-hosted black-box probe. Retained as an exit option if the
  managed candidate fails account, export, retention, or schedule validation;
  deferred because it adds another host and service to operate.

Implementation status:

Provider comparison completed. Account availability was validated and three
non-authoritative shadow checks were configured on 2026-09-04 under sanitized
monitor revision `v2-4-shadow-001`. The local normalized exporter and evaluator
are implemented. `API-01` remains `Draft`: live Metrics API proof, independent
archive implementation and recovery, provider failure-detail and alert tests,
and a complete 24-hour shadow run are required next. The effective configuration
is in `docs/v2.4-synthetic-monitoring-rollout.md`; decision details and vendor
references are in `docs/v2.4-synthetic-monitoring-provider-decision.md`.
