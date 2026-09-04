# GoldSrcOps Architecture

GoldSrcOps is currently a modular monolith. The API host owns HTTP endpoints,
background polling, command and alert dispatch, snapshot and outbox retention,
persistence wiring, health checks, and metrics export in one deployable
process. The internal boundaries still separate API contracts, application
orchestration, domain rules, and infrastructure integrations.

## System Overview

```mermaid
flowchart LR
    clients["Operators / API clients"]
    collector["OpenTelemetry Collector"]
    prometheus["Prometheus"]
    grafana["Grafana"]
    goldsrc["GoldSrc dedicated servers"]
    webhook["HTTPS webhook receiver"]
    postgres[("PostgreSQL")]

    subgraph api["GoldSrcOps.Api"]
        endpoints["Server, command, credential, incident, and dashboard endpoints"]
        probes["/health/live, /health/ready, /metrics"]
        openapi["OpenAPI in Development"]
    end

    subgraph application["GoldSrcOps.Application"]
        serverService["ServersService"]
        monitoringService["MonitoringReadService"]
        incidentService["IncidentsService"]
        credentialService["ServerCredentialsService"]
        commandService["CommandExecutionService"]
        commandDispatcher["CommandDispatcher"]
        commandExecutor["IRconCommandExecutor"]
        alertDispatcher["AlertDispatcher"]
        alertChannel["IAlertDeliveryChannel"]
        pollingService["ServerPollingService"]
        retentionService["SnapshotRetentionService"]
        telemetry["GoldSrcOpsMetrics"]
    end

    subgraph domain["GoldSrcOps.Domain"]
        server["Server"]
        currentState["ServerCurrentState"]
        snapshot["PollSnapshot"]
        incident["AvailabilityIncident"]
        credential["ServerCredential"]
        command["CommandExecution"]
    end

    subgraph infrastructure["GoldSrcOps.Infrastructure"]
        dbContext["GoldSrcOpsDbContext"]
        repositories["EF repositories"]
        outboxStore["EF outbox writer and store"]
        a2sClient["GoldSrcServerQueryClient"]
        rconExecutor["GoldSrcRconCommandExecutor"]
        rconClient["GoldSrcRconClient"]
        webhookChannel["HttpWebhookAlertDeliveryChannel"]
        secretResolver["ConfigurationSecretReferenceResolver"]
        backgroundWorker["GoldSrcPollingBackgroundService"]
        commandWorker["CommandDispatchBackgroundService"]
        alertWorker["AlertDispatchBackgroundService"]
        retentionWorker["SnapshotRetentionBackgroundService"]
    end

    clients --> endpoints
    telemetry -->|OTLP metrics| collector
    prometheus -->|scrapes| collector
    grafana --> prometheus
    endpoints --> serverService
    endpoints --> monitoringService
    endpoints --> incidentService
    endpoints --> credentialService
    endpoints --> commandService
    probes --> dbContext

    backgroundWorker --> pollingService
    pollingService --> a2sClient
    pollingService --> repositories
    pollingService --> outboxStore
    pollingService --> telemetry
    commandWorker --> commandDispatcher
    alertWorker --> alertDispatcher
    alertDispatcher --> outboxStore
    alertDispatcher --> alertChannel
    alertDispatcher --> telemetry
    alertChannel --> webhookChannel
    retentionWorker --> retentionService
    retentionService --> repositories
    retentionService --> telemetry

    serverService --> repositories
    monitoringService --> repositories
    incidentService --> repositories
    credentialService --> repositories
    commandService --> repositories
    commandDispatcher --> repositories
    commandDispatcher --> commandExecutor
    commandExecutor --> rconExecutor
    rconExecutor --> secretResolver
    rconExecutor --> rconClient

    repositories --> dbContext
    outboxStore --> dbContext
    dbContext --> postgres
    a2sClient --> goldsrc
    rconClient --> goldsrc
    webhookChannel --> webhook

    serverService -. uses .-> domain
    credentialService -. uses .-> domain
    commandService -. creates .-> domain
    commandDispatcher -. updates .-> domain
    pollingService -. updates .-> domain
    repositories -. persists .-> domain
```

## Project Boundaries

- `GoldSrcOps.Api` contains the host, JWT bearer authentication, endpoint
  authorization policies, Minimal API endpoint mapping, health
  endpoints, Prometheus scraping endpoint, and OpenTelemetry configuration.
- `GoldSrcOps.Contracts` contains HTTP request and response records that define
  the public API shape.
- `GoldSrcOps.Application` coordinates use cases such as server registration,
  credential metadata, command queueing, monitoring reads, incident reads, and
  polling, alert dispatch, and snapshot retention. It owns the outbox and alert
  delivery boundaries and the provider-independent availability normalizer and
  expected-slot evaluator, but depends on interfaces, not EF Core, UDP, or HTTP
  transport details.
- `GoldSrcOps.Domain` owns the core server state model and transition rules:
  server status, poll snapshots, availability incidents, credential references,
  and command execution records.
- `GoldSrcOps.Infrastructure` implements EF Core persistence, PostgreSQL
  configuration, the GoldSrc A2S query client, the GoldSrc RCON executor/client,
  the HTTPS webhook channel, local secret-reference resolution, the system
  clock, and the polling, command-dispatch, alert-dispatch, and retention
  background workers.
- `GoldSrcOps.AvailabilityExporter` is a separately invoked operator tool. It
  reads bounded managed-monitor metrics, writes create-only canonical JSONL,
  and runs the application-layer evaluator outside the production API runtime.
  Provider credentials are process environment inputs and raw evidence remains
  outside PostgreSQL and Git.

## Security Boundary

The API enforces the control-plane security boundary defined in
`docs/security.md`.

```mermaid
flowchart LR
    client["Operator / API client"]
    identity["External OAuth 2.0 / OIDC provider"]
    authentication["JWT bearer authentication"]
    reader["Reader policy"]
    operatorPolicy["Operator policy"]
    reads["Read endpoints and /metrics"]
    writes["Server, credential, and command mutations"]
    platform["Container / platform probe"]
    health["Anonymous health probes"]

    client -->|"OAuth/OIDC flow"| identity
    identity -->|"JWT access token"| client
    client -->|"Authorization: Bearer token"| authentication
    authentication --> reader
    authentication --> operatorPolicy
    reader --> reads
    operatorPolicy --> reads
    operatorPolicy --> writes
    platform --> health
```

GoldSrcOps remains a resource server and does not issue production tokens or
store user accounts. Production token issuance belongs to an external identity
provider. Local development uses project-specific tokens from
`dotnet user-jwts` only.

The `Reader` policy permits read endpoints and metrics. The `Operator` policy
permits all reads plus server, credential, and command mutations. The fallback
policy is `Operator`, so a new endpoint is not exposed to readers by accident.
Only bounded liveness and readiness probes remain anonymous.

Command audit identity comes from the authenticated token subject. Callers can
no longer supply `RequestedBy` in command request bodies. The endpoint matrix,
HTTP behavior, and required security tests are specified in `docs/security.md`.

## Deployment Model

The current deployment runs exactly one active polling worker. If multiple API
instances are deployed, only one may use `Polling:Enabled=true`; HTTP-only
instances must set `Polling__Enabled=false`. The current polling scheduler does
not use a distributed claim, so running multiple active pollers could duplicate
snapshots and incident transitions.

This constraint does not apply to command dispatch. PostgreSQL atomically
claims commands and serializes each server queue, so multiple command workers
or API instances may dispatch commands concurrently. Horizontal polling will
require an expiring database claim or lease before this singleton constraint can
be removed.

Alert dispatch is also multi-instance safe. PostgreSQL atomically claims due
outbox rows, conditional completion uses the active claim ID, and older pending
or processing messages preserve ordering per incident. Concurrency is bounded
per process, so receiver capacity planning must include every enabled replica.

Snapshot cleanup is row-level idempotent, but multiple active retention workers
would perform redundant selection and can contend on the same oldest rows. A
multi-replica deployment should therefore enable `SnapshotRetention` on one
worker process only. This is an efficiency constraint rather than a data
correctness boundary.

EF Core stores `__EFMigrationsHistory` explicitly in `public`. PostgreSQL's
default `$user, public` search path would otherwise start resolving an
unqualified history table into the `goldsrcops` application schema after that
schema is created, because the development role has the same name. Pinning the
history schema keeps repeated migration runs idempotent.

## Runtime Flows

### Register Server

```mermaid
sequenceDiagram
    participant Client
    participant API as GoldSrcOps.Api
    participant App as ServersService
    participant Repo as EfServerRepository
    participant Db as PostgreSQL

    Client->>API: POST /api/servers
    API->>App: RegisterServerCommand
    App->>Repo: Add Server aggregate
    Repo->>Db: Insert server and unknown current state
    API-->>Client: 201 Created
```

### Polling Success

```mermaid
sequenceDiagram
    participant Worker as GoldSrcPollingBackgroundService
    participant App as ServerPollingService
    participant A2S as GoldSrcServerQueryClient
    participant Domain as Domain model
    participant Repo as EF repositories
    participant Db as PostgreSQL

    Worker->>App: PollDueServersAsync
    App->>Repo: Load enabled due servers
    App->>A2S: A2S_INFO query
    A2S-->>App: GameServerInfo
    App->>Domain: MarkOnline and create reachable PollSnapshot
    App->>Repo: Add snapshot and close open incident if present
    Repo->>Db: Save current state, snapshot, incident changes
```

Map, version, and failure text from the external server are normalized and
bounded by Domain before persistence. The EF Core model uses the same limits so
a malformed response cannot exceed the current-state, snapshot, or incident
columns.

### Polling Failure And Incident Detection

```mermaid
sequenceDiagram
    participant Worker as GoldSrcPollingBackgroundService
    participant App as ServerPollingService
    participant A2S as GoldSrcServerQueryClient
    participant Domain as Domain model
    participant Repo as EF repositories
    participant Db as PostgreSQL

    Worker->>App: PollDueServersAsync
    App->>A2S: A2S_INFO query
    A2S-->>App: Timeout or query error
    App->>Domain: MarkOffline and create unreachable PollSnapshot
    App->>Domain: Open AvailabilityIncident after failure threshold
    App->>Repo: Persist state, snapshot, and incident
    Repo->>Db: Save changes
```

When an incident opens or closes, polling serializes a versioned alert payload
and stages an outbox row in the same EF Core transaction as the incident
transition. A rollback removes both changes, and the uniqueness constraint
prevents duplicate event types for the same incident.

### Deliver Incident Alert

```mermaid
sequenceDiagram
    participant Poller as ServerPollingService
    participant UnitOfWork as Monitoring unit of work
    participant OutboxWriter as IOutboxWriter
    participant Db as PostgreSQL
    participant Worker as AlertDispatchBackgroundService
    participant Dispatcher as AlertDispatcher
    participant Store as IOutboxStore
    participant Webhook as IAlertDeliveryChannel
    participant Receiver as HTTPS receiver

    Poller->>UnitOfWork: Begin transaction
    Poller->>OutboxWriter: Stage versioned incident event
    UnitOfWork->>Db: Commit incident transition and outbox row atomically
    Worker->>Dispatcher: DispatchNextAsync
    Dispatcher->>Store: Atomically claim oldest eligible message
    Store->>Db: Mark Processing with claim ID and attempt count
    Dispatcher->>Webhook: Deliver one POST with stable Idempotency-Key
    Webhook->>Receiver: HTTPS request
    Receiver-->>Webhook: Status and optional Retry-After
    Dispatcher->>Store: Conditionally process, retry, or dead-letter claim
    Store->>Db: Persist outcome only for active claim ID
```

The webhook call runs outside the database transaction. This gives at-least-once
delivery: an ambiguous network outcome may be retried, and the receiver must
deduplicate by event ID. Expired claims are recovered, final attempts move
directly to dead letter, and processed rows are deleted in bounded batches. See
`docs/v2-alert-outbox.md` and `docs/alert-delivery.md` for the complete protocol.

### Queue And Execute Command

```mermaid
sequenceDiagram
    participant Client
    participant API as GoldSrcOps.Api
    participant App as CommandExecutionService
    participant Worker as CommandDispatchBackgroundService
    participant Dispatcher as CommandDispatcher
    participant Executor as IRconCommandExecutor
    participant Secrets as Secret resolver
    participant RCON as GoldSrc RCON
    participant Repo as EF repositories
    participant Domain as Domain model
    participant Db as PostgreSQL

    Client->>API: POST /api/servers/{id}/commands/say (Operator token)
    API->>App: CreateCommandExecutionCommand with token subject
    App->>Repo: Check server and RCON credential metadata
    App->>Domain: Create pending CommandExecution
    Repo->>Db: Insert command execution
    API-->>Client: 201 Created

    Worker->>Dispatcher: DispatchNextAsync
    Dispatcher->>Repo: Atomically claim oldest eligible Pending command
    Repo->>Db: Lock server row and mark command Running
    Repo-->>Dispatcher: Command, endpoint, and credential reference
    Dispatcher->>Executor: Execute RCON command using credential reference
    Executor->>Secrets: Resolve RconSecrets alias
    Executor->>RCON: challenge rcon and rcon command over UDP
    Executor-->>Dispatcher: Succeeded, Failed, or TimedOut
    Dispatcher->>Domain: Mark command Succeeded or Failed
    Dispatcher->>Repo: Conditionally complete the same Running claim
    Repo->>Db: Save final status
```

The PostgreSQL claim locks the server row with `FOR UPDATE SKIP LOCKED`. This
allows workers and API replicas to execute commands for different servers in
parallel while serializing each server queue. A stale `Running` command is
marked `Failed` rather than retried automatically because RCON does not provide
an idempotency key; conditional completion prevents a late worker from
overwriting that recovery result.

The credential API accepts a validated alias. Application stores it as a
canonical `rcon-secret://<alias>` reference, and Infrastructure resolves only
the matching `RconSecrets:<alias>` configuration key. Raw RCON passwords are not
stored in the database or returned by API contracts. Arbitrary environment or
configuration paths cannot be selected through the API. Missing, legacy, or
unsupported references fail the command before a network packet is sent.

### Snapshot Retention

```mermaid
sequenceDiagram
    participant Worker as SnapshotRetentionBackgroundService
    participant App as SnapshotRetentionService
    participant Repo as EfPollSnapshotRetentionRepository
    participant Db as PostgreSQL

    Worker->>App: CleanupAsync
    App->>App: cutoff = UtcNow - RetentionPeriod
    App->>Repo: DeleteBatchOlderThanAsync(cutoff, batchSize)
    Repo->>Db: Delete oldest rows where CheckedAtUtc < cutoff
    Db-->>Repo: Deleted row count (at most batchSize)
    Repo-->>App: Deleted row count
    App-->>Worker: Cutoff, count, and batch-limit signal
```

Each pass executes one bounded delete. Ordering by `CheckedAtUtc` and `Id`
makes backlog draining deterministic, and the strict cutoff retains a snapshot
whose timestamp equals the boundary. Current state and availability incidents
are stored separately and are not part of the delete. See
`docs/snapshot-retention.md` for configuration and capacity guidance.

## Observability

- `/health/live` is a lightweight liveness probe and intentionally does not run
  dependency checks.
- `/health/ready` runs readiness checks tagged as `ready`, including database
  connectivity through `GoldSrcOpsDbContext`.
- `/metrics` exposes ASP.NET Core, runtime, and GoldSrcOps application metrics
  in Prometheus format through OpenTelemetry. It remains authenticated for v2
  compatibility and rollback.
- The production-default path exports metrics by private OTLP gRPC to the
  OpenTelemetry Collector. Prometheus scrapes only the Collector's private
  exporter and internal telemetry endpoints, and Grafana queries Prometheus.
  None of those service ports is published on the host.
- Application metrics currently cover polling runs, server poll attempts by
  result, incident transitions, queued commands, dispatched commands, completed
  command dispatches by result, recovered interrupted commands, and
  snapshot-retention runs, deletions, failures, and duration. Alert instruments
  cover enqueueing, attempts, delivery, retries, dead letters, expired-claim
  recovery, duration, backlog count and age, and processed-row deletion.
- Structured RCON lifecycle events expose safe command and server identifiers,
  type, status, result, and duration while excluding payloads, secret references,
  passwords, and executor response text.
- Command dispatch remains auditable through `CommandExecution` records in
  addition to the Prometheus metrics.

## Testing Shape

The current test suite keeps the layers visible:

- domain and application unit tests cover state transitions and orchestration
  rules;
- API integration tests cover endpoint behavior through `WebApplicationFactory`;
- deterministic polling integration tests replace the A2S query client and
  clock while using production DI and EF-backed repositories;
- command execution tests cover background dispatch orchestration, secret-reference
  resolution, structured-log safety, GoldSrc RCON packet handling, and a
  synthetic UDP RCON flow;
- telemetry tests cover metric recording and direct Prometheus exposure, while
  the production container smoke observes application and ASP.NET Core metrics
  after the private Collector boundary and checks Collector-outage readiness;
- alert tests cover transactional enqueueing, dispatcher outcomes, retry and
  dead-letter policy, sanitized logs, the synthetic HTTP boundary, and
  Prometheus exposure;
- PostgreSQL-backed integration tests use Testcontainers and apply EF Core
  migrations against a real PostgreSQL provider, including concurrent
  per-server claims, interrupted-command recovery, and bounded snapshot
  retention with strict cutoff behavior. They also cover alert migration,
  atomic incident/outbox commit, concurrent outbox claims, per-incident
  ordering, recovery, statistics, and retention, and repeat migration
  application when the database role and application schema share a name.
