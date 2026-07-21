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
