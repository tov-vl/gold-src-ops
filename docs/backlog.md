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

## Immediate Next Milestone

Add the first API boundary and protocol parsing test coverage.

Definition of done:

- Integration tests cover `POST /api/servers` and `GET /api/servers/{id}/status`.
- Unit tests cover A2S packet parsing with captured byte arrays.
- Unit tests cover core server state transition rules.
- Test setup stays lightweight and can later evolve to Testcontainers for PostgreSQL.

## Next 10 Tasks

1. Add integration tests for `POST /api/servers` and `GET /api/servers/{id}/status`.

2. Add unit tests for A2S packet parsing with captured byte arrays.

3. Add unit tests for state transition rules.

4. Add Docker-based end-to-end smoke test notes for polling against a live server.

5. Add CI workflow for format, build, test, and package vulnerability checks.

6. Add API integration test coverage for `GET /api/servers/{id}/snapshots`.

7. Add API integration test coverage for `GET /api/dashboard/overview`.

8. Add readiness health check that validates database connectivity.

9. Add basic metrics endpoint.

10. Add architecture diagram or concise text diagram to README/docs.

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
