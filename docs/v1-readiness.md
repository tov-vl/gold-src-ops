# GoldSrcOps v1 Readiness

Review date: 2026-08-19. Publication date: 2026-08-21.

Outcome: the implemented scope satisfies the MVP in `docs/project-brief.md`.
No unresolved v1 blocker remains after the fixes recorded below. The verified
behavior is packaged in `docs/demo.md` and `docs/release-notes-v1.md` and was
published as the
[GoldSrcOps v1.0.0 release](https://github.com/tov-vl/gold-src-ops/releases/tag/v1.0.0)
from a signed tag on 2026-08-21.

## MVP Evidence Matrix

| MVP capability | Implementation evidence | Verification evidence | Status |
| --- | --- | --- | --- |
| Register and manage GoldSrc servers | `ServerEndpoints`, `ServersService`, `Server`, and EF repositories | API and PostgreSQL endpoint integration tests; authenticated local smoke | Ready |
| Poll status through A2S on a schedule | `GoldSrcPollingBackgroundService`, `ServerPollingService`, and `GoldSrcServerQueryClient` | Packet/parser tests, deterministic polling integration tests, and a live `A2S_INFO` smoke result | Ready |
| Store current state and snapshot history | `ServerCurrentState`, `PollSnapshot`, `GoldSrcOpsDbContext`, and monitoring reads | Domain tests, API/PostgreSQL integration tests, and local `/status` plus `/snapshots` smoke | Ready |
| Bound snapshot storage | `SnapshotRetentionService`, bounded PostgreSQL delete, retention worker, and concurrent cleanup index | Unit, Prometheus endpoint, and PostgreSQL Testcontainers retention tests | Ready |
| Detect availability incidents | `AvailabilityIncident`, polling transition rules, and incident endpoints | Domain and deterministic failure/recovery integration tests | Ready |
| Expose status and history through REST | Server, monitoring, incident, dashboard, credential, and command endpoint groups | `WebApplicationFactory` policy/contract tests, OpenAPI, and authenticated local smoke | Ready |
| Authenticate readers and operators | JWT bearer validation, `Reader`/`Operator` policies, fallback policy, and token-subject validation | Security unit/API tests plus anonymous `401` and Operator `200` smoke checks | Ready |
| Execute auditable RCON commands | Durable command queue, PostgreSQL claims, per-server serialization, external secret references, and live RCON adapter | Dispatcher, protocol, synthetic UDP, API, and PostgreSQL tests; safe missing-port smoke with subject-derived `RequestedBy` | Ready |
| Expose health and metrics | Anonymous liveness/readiness, database readiness check, OpenTelemetry metrics, and authenticated Prometheus endpoint | Health/metrics integration tests and local scrape of polling, command, and retention series | Ready |
| Provide a repeatable local workflow | Docker Compose, EF migrations, local JWT helper, startup script, README, and smoke guide | PowerShell 7/5.1 parsing and the runtime checks below | Ready |

## Runtime Verification

The documented local path was executed against PostgreSQL 16 in Docker:

```powershell
.\tools\dev\start-local.ps1 -NoRun
```

It completed Docker health validation, solution restore with the selected SDK,
local-tool restore, and every EF Core migration, including the concurrent
`(CheckedAtUtc, Id)` retention index. A second migration run completed without
reapplying any migration.

A separate Development API process was then exercised with a short-lived local
Operator JWT. The token itself was never logged or persisted in the repository.
Observed results:

- `/health/live` and `/health/ready`: `200`;
- anonymous `/metrics` and Development OpenAPI: `401`;
- authenticated Development OpenAPI: `200`;
- server register, update, disable, and enable operations succeeded;
- live polling of `server.csomod.com:27015` reached `Online` and persisted a
  snapshot;
- dashboard and incident reads succeeded, with no incident during the successful
  live poll;
- credential response contained metadata only;
- a queued `say` command without an RCON port failed safely before executor or
  network dispatch with `RCON port is not configured.`;
- persisted `RequestedBy` matched the authenticated token subject;
- polling, command queue/completion, and snapshot-retention metrics were present.

Production startup without `ConnectionStrings__GoldSrcOps` was also verified to
fail fast. There is no fallback to the tracked Development database password.

## Quality Gate

The repository gate consists of:

```powershell
dotnet restore GoldSrcOps.sln -p:AuditPipeline=true
dotnet format GoldSrcOps.sln --verify-no-changes --no-restore
dotnet build GoldSrcOps.sln --no-restore
dotnet test GoldSrcOps.sln --no-build
dotnet list GoldSrcOps.sln package --vulnerable --include-transitive
```

The final readiness run passed all 141 tests with no build warnings and no known
vulnerable direct or transitive NuGet packages.

## Findings Closed

- `start-local.ps1` now restores solution packages with the SDK it selected,
  preventing stale cross-feature-band `project.assets.json` failures.
- Local `dotnet-ef` invocation now preserves the separator required to forward
  `--environment Development`.
- Local Bearer issuer/audience settings now live in ignored
  `appsettings.Local.json` through `tools/dev/new-local-jwt.ps1`; token creation
  no longer dirties tracked config.
- The base appsettings file no longer contains a Development PostgreSQL password.
- EF Core migration history is explicitly stored in `public`, so repeated
  startup remains idempotent when the PostgreSQL role and application schema
  are both named `goldsrcops`.
- API test factories provide their required connection string during host
  bootstrap and no longer depend on a value from tracked appsettings.
- `GoldSrcOps.Api.http` now targets real authenticated endpoints instead of the
  removed template weather endpoint.
- Smoke documentation now distinguishes a queued command from a command that
  actually reached the RCON executor.
- The portfolio description says availability incidents rather than claiming
  an alert-delivery capability that belongs to a later version.

## Accepted Deferrals

- A real RCON smoke remains manual and restricted to a server the operator owns.
  The guarded helper, protocol tests, synthetic UDP tests, and safe failure smoke
  provide v1 evidence without sending an unsolicited command.
- Production identity-provider, reverse-proxy, and secret-store wiring are
  deployment responsibilities. The application validates their required
  configuration and has no production auth bypass.
- Multiple active polling workers require a distributed lease; v1 intentionally
  runs one poller. Retention is also enabled on one worker to avoid redundant
  cleanup contention.
- Alert delivery and an outbox are v2 candidates. v1 exposes incidents but does
  not claim notification delivery.
- Distributed tracing and a packaged Grafana dashboard are useful follow-ups,
  not MVP blockers; v1 has structured logs, health checks, runtime/application
  metrics, and Prometheus export.
