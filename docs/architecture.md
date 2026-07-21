# GoldSrcOps Architecture

GoldSrcOps is currently a modular monolith. The API host owns HTTP endpoints,
background polling, persistence wiring, health checks, and metrics export in one
deployable process. The internal boundaries still separate API contracts,
application orchestration, domain rules, and infrastructure integrations.

## System Overview

```mermaid
flowchart LR
    clients["Operators / API clients"]
    prometheus["Prometheus"]
    goldsrc["GoldSrc dedicated servers"]
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
        commandExecutor["IRconCommandExecutor"]
        pollingService["ServerPollingService"]
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
        a2sClient["GoldSrcServerQueryClient"]
        rconExecutor["GoldSrcRconCommandExecutor"]
        rconClient["GoldSrcRconClient"]
        secretResolver["ConfigurationSecretReferenceResolver"]
        backgroundWorker["GoldSrcPollingBackgroundService"]
    end

    clients --> endpoints
    prometheus --> probes
    endpoints --> serverService
    endpoints --> monitoringService
    endpoints --> incidentService
    endpoints --> credentialService
    endpoints --> commandService
    probes --> dbContext

    backgroundWorker --> pollingService
    pollingService --> a2sClient
    pollingService --> repositories
    pollingService --> telemetry

    serverService --> repositories
    monitoringService --> repositories
    incidentService --> repositories
    credentialService --> repositories
    commandService --> repositories
    commandService --> commandExecutor
    commandExecutor --> rconExecutor
    rconExecutor --> secretResolver
    rconExecutor --> rconClient

    repositories --> dbContext
    dbContext --> postgres
    a2sClient --> goldsrc
    rconClient --> goldsrc

    serverService -. uses .-> domain
    credentialService -. uses .-> domain
    commandService -. creates .-> domain
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
  polling. It depends on repository and protocol interfaces, not EF Core or UDP
  details.
- `GoldSrcOps.Domain` owns the core server state model and transition rules:
  server status, poll snapshots, availability incidents, credential references,
  and command execution records.
- `GoldSrcOps.Infrastructure` implements EF Core persistence, PostgreSQL
  configuration, the GoldSrc A2S query client, the GoldSrc RCON executor/client,
  local secret-reference resolution, the system clock, and the background
  polling worker.

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

### Queue And Dispatch Command

```mermaid
sequenceDiagram
    participant Client
    participant API as GoldSrcOps.Api
    participant App as CommandExecutionService
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

    Client->>API: POST /api/commands/{commandId}/dispatch
    API->>App: Dispatch command
    App->>Repo: Load command, server endpoint, and credential reference
    App->>Domain: Mark command Running
    Repo->>Db: Save Running status
    App->>Executor: Execute RCON command using credential reference
    Executor->>Secrets: Resolve RconSecrets alias
    Executor->>RCON: challenge rcon and rcon command over UDP
    Executor-->>App: Succeeded, Failed, or TimedOut
    App->>Domain: Mark command Succeeded or Failed
    Repo->>Db: Save final status
    API-->>Client: 200 OK
```

The credential API accepts a validated alias. Application stores it as a
canonical `rcon-secret://<alias>` reference, and Infrastructure resolves only
the matching `RconSecrets:<alias>` configuration key. Raw RCON passwords are not
stored in the database or returned by API contracts. Arbitrary environment or
configuration paths cannot be selected through the API. Missing, legacy, or
unsupported references fail the command before a network packet is sent.

## Observability

- `/health/live` is a lightweight liveness probe and intentionally does not run
  dependency checks.
- `/health/ready` runs readiness checks tagged as `ready`, including database
  connectivity through `GoldSrcOpsDbContext`.
- `/metrics` exposes ASP.NET Core, runtime, and GoldSrcOps application metrics
  in Prometheus format through OpenTelemetry.
- Application metrics currently cover polling runs, server poll attempts by
  result, incident transitions, queued commands, dispatched commands, and
  completed command dispatches by result.
- Command dispatch remains auditable through `CommandExecution` records in
  addition to the Prometheus metrics.

## Testing Shape

The current test suite keeps the layers visible:

- domain and application unit tests cover state transitions and orchestration
  rules;
- API integration tests cover endpoint behavior through `WebApplicationFactory`;
- deterministic polling integration tests replace the A2S query client and
  clock while using production DI and EF-backed repositories;
- command execution tests cover fake dispatch orchestration, secret-reference
  resolution, GoldSrc RCON packet handling, and a synthetic UDP RCON flow;
- telemetry tests cover command metric recording and Prometheus exposure;
- PostgreSQL-backed integration tests use Testcontainers and apply EF Core
  migrations against a real PostgreSQL provider.
