# GoldSrcOps Backlog

This backlog tracks completed milestones and the next reviewable development or
release steps.

## Current Status

Completed:

- Repository created at `D:\source\repos\personal\gold-src-ops`.
- Solution created.
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

## Immediate Next Milestone

Document the current architecture and harden deterministic polling integration coverage.

Definition of done:

- README/docs include a concise architecture diagram or text diagram.
- Polling integration tests can run deterministically with a fake query client.
- Integration tests cover incident opening after repeated polling failures.

## Next Tasks

1. Add architecture diagram or concise text diagram to README/docs.

2. Add integration test for incident opening after repeated failures.

3. Add fake query client wiring for deterministic polling integration tests.

4. Introduce Testcontainers for PostgreSQL-backed integration tests.

5. Add `PATCH /api/servers/{id}`.

6. Add `POST /api/servers/{id}/enable` and `POST /api/servers/{id}/disable`.

## v1 API Scope

Servers:

- `POST /api/servers`
- `GET /api/servers`
- `GET /api/servers/{id}`
- `PATCH /api/servers/{id}`
- `POST /api/servers/{id}/enable`
- `POST /api/servers/{id}/disable`

Monitoring:

- `GET /api/servers/{id}/status`
- `GET /api/servers/{id}/snapshots?from=&to=`
- `GET /api/dashboard/overview`

Incidents:

- `GET /api/incidents/open`
- `GET /api/servers/{id}/incidents`
- `GET /api/incidents/{id}`

Later command scope:

- `POST /api/servers/{id}/commands/change-map`
- `POST /api/servers/{id}/commands/restart`
- `POST /api/servers/{id}/commands/say`
- `POST /api/servers/{id}/commands/raw`
- `GET /api/servers/{id}/commands`
- `GET /api/commands/{commandId}`

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

Future entities:

- `ServerCredential`
- `PlayerSnapshot`
- `CommandExecution`
- `AlertDelivery`
- `AuditEntry`

## Testing Plan

Start focused:

- Unit tests for A2S packet parsing with captured byte arrays.
- Unit tests for state transition rules.
- Integration tests for `POST /api/servers` and `GET /api/servers/{id}/status`.

Later:

- Testcontainers for PostgreSQL.
- Fake query client for deterministic polling tests.
- Integration test for incident opening after repeated failures.

## Portfolio Readiness Checklist

Before calling v1 done:

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
