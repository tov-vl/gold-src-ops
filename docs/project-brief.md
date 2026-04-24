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

> GoldSrcOps is a backend control plane for GoldSrc dedicated servers. It polls game servers via A2S, executes operator actions through RCON, stores availability history, and exposes health, metrics, and alerts for operational visibility.

Avoid positioning it as:

- A generic monitoring platform for every game.
- A simple CS 1.6 website.
- A microservices demo.

## Current Repository State

Location:

```text
D:\source\repos\personal\gold-src-ops
```

Current implementation:

- Git repository initialized.
- `GoldSrcOps.sln` created.
- `src/GoldSrcOps.A2SSpike` created as a .NET 10 console app.
- A2S `A2S_INFO` spike implemented.
- Live query verified against `server.csomod.com:27015`.

Current command:

```powershell
dotnet run --project .\src\GoldSrcOps.A2SSpike -- server.csomod.com:27015 --timeout 5000 --encoding windows-1251
```

Verified example result:

```text
Server:      [ZOMBIES]+[CSO MOD] [#1] CSOMOD.COM [since 2012]
Map:         zm_csdark_cinder
Players:     28/32 (2 bots)
Latency:     ~140-170 ms
```

## MVP Goal

Build a modular ASP.NET Core backend that can:

- Register GoldSrc servers.
- Poll their status via A2S on a schedule.
- Store current server state.
- Store historical polling snapshots.
- Detect offline incidents.
- Expose status and history through REST endpoints.
- Later execute safe RCON commands.
- Expose health checks and metrics.

## Non-goals For v1

- Full frontend UI.
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
