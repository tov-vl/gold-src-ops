# GoldSrcOps Backlog

This backlog tracks completed milestones and the next reviewable development or
release steps.

## Current Status

Completed:

- Repository and solution initialized.
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
- `GET /api/incidents/open`, `GET /api/incidents/{id}`, and bounded
  `GET /api/servers/{id}/incidents?limit=` added.
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
- Deterministic polling integration tests added with fake A2S query responses and EF-backed repositories.
- Integration tests cover incident opening after repeated polling failures.
- Architecture overview and runtime flow diagrams added to `docs/architecture.md`.
- PostgreSQL-backed integration tests added with Testcontainers and EF Core migrations.
- `PATCH /api/servers/{id}` added for editing server connection details and polling settings.
- `POST /api/servers/{id}/enable` and `POST /api/servers/{id}/disable` added.
- Disabled servers are skipped by background polling and covered by deterministic integration tests.
- Local startup and migration workflow documented in README and smoke-test docs.
- `tools/dev/start-local.ps1` added for local PostgreSQL startup, EF migration, and API launch.
- `ServerCredential` added with external secret references instead of persisted plaintext secrets.
- `CommandExecution` added with command type, status, payload, requester, and execution timestamps.
- Command and credential endpoints added for RCON credential metadata, queuing commands, and reading command history.
- PostgreSQL migration added for `server_credentials` and `command_executions`.
- Unit, API integration, and PostgreSQL-backed integration tests added for the command foundation.
- `IRconCommandExecutor` boundary added for safe command dispatch.
- Pending commands are executed by a background dispatcher that transitions them through `Running` into `Succeeded` or `Failed`.
- Deterministic tests cover successful fake dispatch, executor failure, timeout, missing RCON port, lost completion claims, and PostgreSQL status persistence.
- RCON credentials now use validated aliases stored as canonical `rcon-secret://<alias>` references.
- Secret resolution is restricted to the dedicated `RconSecrets:<alias>` namespace; arbitrary environment and configuration keys are rejected.
- Live GoldSrc RCON client added behind `IRconCommandExecutor` with challenge/command handling, timeout mapping, authentication failure handling, and sanitized result summaries.
- Focused protocol, client, resolver, and executor tests added for command dispatch.
- Command execution metrics added for queued, dispatched, completed, recovered, succeeded, failed, timed-out, and authentication-failed command dispatch paths.
- Authentication, authorization, endpoint policy, and audit-identity model documented in `docs/security.md` and Architecture Decision 9.
- JWT bearer validation and `Reader`/`Operator` policies applied to API, metrics, OpenAPI, and anonymous health probes.
- Command request contracts no longer accept `RequestedBy`; audit identity is derived from the authenticated token subject.
- Unit and API integration tests cover subject validation, the endpoint policy matrix, and requester spoofing protection.
- PostgreSQL atomically claims pending commands, serializes execution per server across workers, and conditionally persists completion for the active claim.
- Interrupted `Running` commands are recovered as `Failed` without automatic RCON retry, and PostgreSQL integration tests cover concurrent claims and recovery.
- Polling metadata and failure reasons are bounded by domain invariants that match the EF Core column limits.
- The v1 singleton-poller deployment constraint and the trigger for distributed polling leases are documented.
- Structured RCON lifecycle logs identify command and server ids, command type,
  status, result, and duration without payload or credential material.
- Guarded local RCON smoke helper added with owned-server acknowledgement,
  authenticated preflight, `-WhatIf`, exact server-id confirmation, and a
  generated `say` command only.
- Configurable poll-snapshot retention added with fail-fast bounds for retention,
  cadence, and batch size.
- A background cleanup worker deletes one oldest PostgreSQL batch per pass while
  preserving snapshots at the cutoff, current server state, and incident history.
- Retention completion, deletion, failure, and duration metrics added with unit,
  Prometheus endpoint, and PostgreSQL Testcontainers coverage.
- A concurrent `(CheckedAtUtc, Id)` index and operational retention guide added.
- The one-command Development startup was verified and repaired to restore with
  its selected SDK and forward EF application arguments correctly.
- Local Bearer issuer/audience settings moved to ignored
  `appsettings.Local.json` through a dedicated helper, leaving tracked
  configuration clean.
- The v1 readiness matrix and runtime evidence were recorded in
  `docs/v1-readiness.md`; no unresolved v1 blocker remains.
- A five-to-ten-minute presenter guide now demonstrates auth, live A2S polling,
  deterministic incident creation, durable command audit, and observability
  without sending RCON traffic to a third-party server.
- Published v1.0.0 release notes summarize delivered scope, reliability and
  security decisions, verification evidence, operational limits, and deferred
  work.
- Public-release hygiene removed local workspace paths and internal process
  wording, replaced the development-log README opening with a release-facing
  summary, and added a documentation map and explicit prerequisites.
- All existing commits have valid SSH signatures, and a high-signal scan of the
  complete Git history found no private-key, JWT, or common provider-token
  patterns. Tracked passwords are limited to documented local Docker defaults
  and test fixtures.
- The public repository license is MIT, with the canonical text and copyright
  notice stored in the root `LICENSE` file.
- The public GitHub repository is published at `tov-vl/gold-src-ops` with
  `main` as its default branch and the release-facing description and topics.
- Private vulnerability reporting, the dependency graph, Dependabot alerts and
  security updates, Secret Protection, and push protection are enabled.
- The active `Protect main` ruleset has no bypasses and requires signed commits,
  linear history, and the GitHub Actions `Quality Gate`, `Container Smoke`, and
  `Browser Smoke`; it also blocks branch deletion and force pushes.
- The initial publication commit was pushed and passed the GitHub Actions
  `Quality Gate`.
- Release documentation was integrated into `main` through a signed linear
  commit, and the required `Quality Gate` passed for the resulting revision.
- The final release checklist was integrated into `main` through a signed
  linear commit, and its required `Quality Gate` passed.
- The signed annotated `v1.0.0` tag identifies the verified release revision,
  and its tag-triggered `Quality Gate` passed.
- The
  [GoldSrcOps v1.0.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v1.0.0)
  is published as the initial stable release.
- The v1.1 container baseline now uses a multi-stage .NET 10 image, runs the
  API as a non-root user on port `8080`, excludes local configuration, and
  keeps EF migration execution outside application startup.
- An isolated container smoke script verifies image contents, production
  configuration fail-fast behavior, separate EF migrations, liveness, and
  PostgreSQL-backed readiness with finally-based Docker resource cleanup.
- GitHub Actions runs the container smoke script in a dedicated job after the
  regular code quality gate succeeds.
- The active `main` ruleset requires both `Quality Gate` and `Container Smoke`
  while retaining signed commits, linear history, deletion protection, and
  force-push protection.
- The v1.1 deployment guide defines immutable image versioning, the runtime and
  configuration contract, singleton-worker topology, separate EF migrations,
  health probes, rollout order, and application/database rollback boundaries.
- OpenTelemetry SDK and instrumentation packages are aligned on stable `1.18.0`,
  and the direct Prometheus exporter is upgraded to the latest available
  `1.18.0-beta.1` with endpoint integration coverage retained.
- Architecture Decision 12 records why v1.1 keeps the authenticated direct
  exporter despite its prerelease status and when to replace it with stable
  OTLP export through an OpenTelemetry Collector.
- The v1.1 readiness matrix records the compatibility review, final local
  quality gate, production container smoke evidence, accepted deployment
  boundaries, and publication prerequisites.
- Published v1.1.0 release notes describe the focused operability delta from
  v1.0.0 without claiming an API or database-schema change.
- The final v1.1 pull request, post-merge `main` run, and signed-tag run passed
  both required GitHub Actions jobs on revision `eb0f02e`.
- The signed annotated `v1.1.0` tag identifies that verified revision, and the
  [GoldSrcOps v1.1.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v1.1.0)
  is published as the preceding stable release.
- Versioned incident-alert contracts, the EF Core outbox model, an additive
  migration, database invariants, and PostgreSQL migration coverage are added.
- Polling transactionally enqueues unavailable and recovered alerts through an
  explicit outbox writer and unit of work. Deterministic and PostgreSQL tests
  cover commit, rollback, and duplicate prevention.
- PostgreSQL atomically claims due outbox messages, conditionally completes or
  reschedules the active claim, recovers expired claims, and preserves ordering
  per incident. Concurrent PostgreSQL tests cover the claim state machine.
- The generic HTTP webhook adapter sends one POST per application attempt with
  a stable idempotency key, classifies retryable and permanent outcomes, honors
  bounded `Retry-After`, applies a request timeout, rejects implicit redirects,
  and never reads response bodies. Synthetic Kestrel tests cover the network
  boundary.
- The hosted alert dispatcher runs each attempt in its own scope, owns bounded
  exponential retry scheduling, dead-letters permanent and exhausted messages,
  recovers expired claims without exceeding the attempt limit, and deletes one
  bounded batch of expired processed rows per cleanup pass. Startup validation,
  safe structured logs, OpenTelemetry counters, duration metrics, and backlog
  gauges are covered by unit, Prometheus, and PostgreSQL tests. Delivery remains
  disabled by default until deployment configuration is supplied.
- Alert-delivery rollout, topology, secret injection, telemetry, recovery, and
  rollback are documented. The production container smoke verifies HTTPS
  configuration fail-fast, enabled-dispatcher startup, endpoint/authorization
  log safety, separate migrations, hardening, and health probes.
- The complete v2 alert-delivery capability was integrated through pull request
  #8 into protected `main` as verified squash commit `2f17aa8`. Required
  `Quality Gate` and `Container Smoke` checks passed before and after merge,
  local `main` was synchronized, and the merged feature branch was removed.
- Published v2.0.0 release notes record delivery semantics, compatibility,
  migration, deployment, verification evidence, and intentional limits.
- Final candidate pull request #10, its post-merge `main` run, and the signed-tag
  run passed `Quality Gate` and `Container Smoke` on revision `9d7176f`.
- The signed annotated `v2.0.0` tag identifies that verified revision, and the
  [GoldSrcOps v2.0.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.0.0)
  is published as an earlier stable release.
- Bounded Reader dead-letter inspection, transactional Operator replay, durable
  audit reads, idempotency, and concurrent-request protection were integrated
  through pull request #15 as verified squash commit `6a8b486`; its required
  pull-request and post-merge checks passed.
- Replay outcome telemetry now exposes only `accepted`, `idempotent`,
  `conflict`, and `invalid` labels. Source-generated lifecycle logs and focused
  unit, API, Prometheus, cancellation, and log-safety tests cover the operator
  recovery path without recording subjects, reasons, payloads, or failure
  details.
- The final local dead-letter replay gate passed audit restore, format
  verification, a zero-warning solution build, all 239 tests, a transitive
  vulnerability report with no findings, and the production container smoke
  against isolated PostgreSQL.
- Pull request #16 integrated replay observability and final operations guidance
  into protected `main` as verified squash commit `bd000b7`. Its required
  pull-request and genuine post-merge `Quality Gate` and `Container Smoke`
  checks passed, local `main` was synchronized, and the merged feature branch
  was removed.
- Published v2.1.0 release notes record the additive inspection and replay API,
  database compatibility, operator workflow, accepted boundaries, and final
  verification evidence without rewriting the published v2.0.0 history.
- Final candidate pull request #17, its post-merge `main` run, and the signed-tag
  run passed `Quality Gate` and `Container Smoke` on revision `af7c2f4`.
- The signed annotated `v2.1.0` tag identifies that verified revision, and the
  [GoldSrcOps v2.1.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.1.0)
  is published as the preceding stable release.
- Published v2.2.0 release notes record bounded multi-datagram RCON response
  collection, endpoint isolation, validated receive ceilings, compatibility,
  owned-server evidence, and residual UDP limits.
- Final release-documentation pull request #23, post-merge run #76, and
  signed-tag run #77 passed `Quality Gate` and `Container Smoke` on revision
  `9e02f07`.
- The signed annotated `v2.2.0` tag identifies that verified revision, and the
  [GoldSrcOps v2.2.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.2.0)
  is published as the latest stable release.
- Post-release documentation pull request #24 synchronized the published v2.2
  status across the repository. Its required checks and genuine post-merge CI
  run #80 passed on protected `main` revision `947d54c`.

## Completed v1.1 Milestone

GoldSrcOps v1.1.0 was published on 2026-08-23 as a focused operability release
without changing the v1 API contract or pulling the deferred v2 outbox work
forward.

Release status: implementation, local readiness evidence, required remote
checks, signed tag, and GitHub Release publication are complete.

Completed definition of done:

- A production-oriented container image packages the API with a non-root
  runtime and leaves EF migration execution as a separate deployment action.
- CI builds the image and smoke-tests it against PostgreSQL using the documented
  configuration contract.
- Deployment documentation covers image versioning, health probes,
  configuration, migrations, and rollback expectations.
- The prerelease Prometheus exporter dependency is reevaluated before v1.1 and
  either upgraded or retained with an explicit current rationale.
- The public API and v1 reliability semantics remain backward compatible.

## Completed v2.0.0 Milestone

The first v2 capability is defined by Decision 13 and
`docs/v2-alert-outbox.md`.

GoldSrcOps v2.0.0 was published on 2026-08-26 as the first supported
incident-alert delivery release.

Release status: implementation, local readiness evidence, required remote
checks, signed tag, and GitHub Release publication are complete.

Completed slices:

1. Add versioned incident-alert contracts, the EF Core outbox model and
   configuration, an additive migration, database constraints, and a
   PostgreSQL migration test.
2. Add the explicit outbox writer and unit of work, then enqueue unavailable
   and recovered events inside the existing incident transaction. Cover commit,
   rollback, and duplicate prevention through polling and PostgreSQL tests.
3. Add the PostgreSQL claim protocol, conditional completion, expiring-claim
   recovery, retry scheduling, per-incident ordering, and concurrent-dispatcher
   integration tests.
4. Add the generic HTTP webhook adapter and synthetic-server tests for
   idempotency headers, status classification, timeouts, bounded responses, and
   one request per application attempt.
5. Add the hosted dispatcher, validated configuration, OpenTelemetry metrics,
   sanitized structured logs, dead-letter behavior, and bounded processed-row
   retention.
6. Complete rollout and operations documentation, then run the full local
   quality gate and production container smoke test.

All six implementation slices and the protected-main integration workflow are
complete. Pull request #8 integrated the capability, pull request #9 recorded
its readiness, and final candidate pull request #10 integrated the release
notes. The post-merge `main` and signed-tag workflows passed on `9d7176f`, and
the stable GitHub Release is published. Evidence and accepted boundaries are
recorded in `docs/v2-readiness.md`.

Published repository: [tov-vl/gold-src-ops](https://github.com/tov-vl/gold-src-ops)

Configured GitHub description:

> Production-minded .NET 10 control plane for monitoring and administering
> GoldSrc servers through A2S and RCON.

Configured topics: `dotnet`, `aspnet-core`, `postgresql`, `opentelemetry`,
`goldsrc`, `counter-strike`, `a2s`, `rcon`, and `testcontainers`.

## Completed v2.1.0 Milestone

GoldSrcOps v2.1.0 was published on 2026-08-27 as a backward-compatible
dead-letter recovery release. Its accepted contract and operational boundaries
are documented in `docs/dead-letter-replay.md` and `docs/alert-delivery.md`.

Release status: implementation, local readiness evidence, required remote
checks, signed tag, and GitHub Release publication are complete.

Completed slices:

1. Add replay metadata, append-only audit persistence, constraints, indexes,
   and a new additive PostgreSQL migration.
2. Add bounded `Reader` inspection endpoints with cursor pagination and a
   newer-event ordering warning.
3. Add the single-message `Operator` replay endpoint with stable event identity,
   explicit idempotency, atomic audit, and concurrent-request protection.
4. Add replay outcome metrics and sanitized lifecycle logs, complete final
   operations guidance, and run the full release-gate verification.

All four implementation slices and the protected-main integration workflow are
complete. Pull request #15 integrated the audited replay capability, pull
request #16 completed observability and operations guidance, and final candidate
pull request #17 integrated the release notes. The post-merge `main` and
signed-tag workflows passed on `af7c2f4`, and the stable GitHub Release is
published. Evidence and accepted boundaries are recorded in
`docs/v2.1-readiness.md`.

A second delivery channel, broker, service extraction, bulk replay, and a
distributed polling claim remain deferred until their scaling, receiver, or
ownership requirements become concrete.

## Completed v2.2.0 Milestone

GoldSrcOps v2.2.0 was published on 2026-08-28 as a backward-compatible RCON
reliability release. The design and accepted protocol limits are documented in
`docs/v2.2-rcon-response-reliability.md`; release notes and evidence are in
`docs/release-notes-v2.2.md` and `docs/v2.2-readiness.md`. Published `v2.1.0`
is the preceding stable release.

Release status: implementation, owned-server evidence, local readiness gates,
required remote checks, signed tag, and GitHub Release publication are
complete.

Why this work was selected:

- The pre-v2.2 RCON client read exactly one command-response datagram even though
  a server can flush longer console output through several ordinary
  `A2A_PRINT` datagrams.
- The reliability slice prevents this known partial-success path within
  documented receive bounds.
- The fix stays inside the current modular-monolith and RCON boundaries. It
  requires no public API or database-schema change.
- Deferred broker, second-channel, bulk-replay, service-extraction, and
  distributed-polling work still lacks a concrete scaling or ownership need.

Completed and integrated slices:

1. Preserve `A2A_PRINT` chunk boundaries until final normalization and assemble
   single or multi-datagram responses in receive order.
2. Add a bounded response collector, one end-to-end deadline, and a connected
   UDP socket without automatic command retry.
3. Validate the quiet interval, datagram ceiling, and aggregate wire-byte
   ceiling while retaining compatible defaults when the settings are omitted.
4. Cover quiet completion, cancellation, malformed responses, response
   ceilings, continuous response flow, first-response timeout, and endpoint
   isolation with synthetic UDP tests.
5. Update tracked defaults, deployment guidance, and RCON operations guidance.
6. Verify guarded `say` dispatch plus multi-datagram timing and framing with a
   read-only `cvarlist` command against an isolated local ReHLDS 3.14.0.857
   instance.
7. Integrate the implementation through protected `main` in pull request #20
   and pass its required pull-request and genuine post-merge checks on
   `f6baf40`.

All seven implementation and owned-server verification slices are complete.
Pull request #20 integrated the implementation, pull request #22 corrected the
timing-sensitive test exposed by the first candidate post-merge run, and pull
request #23 integrated the final release documentation. Post-merge run #76 and
signed-tag run #77 passed on `9e02f07`, and the stable GitHub Release is
published. Post-release pull request #24 recorded that publication and passed
post-merge CI run #80 on `947d54c`. Evidence and accepted boundaries are
recorded in `docs/v2.2-readiness.md`.

Definition of done:

- Existing single-datagram command behavior remains backward compatible.
- Multi-datagram text is assembled in receive order within documented bounds.
- Known partial responses fail explicitly instead of being persisted as a
  successful truncated result.
- Response text, command payloads, and credentials remain absent from logs and
  metric dimensions.
- Public API, authorization, and database contracts remain unchanged.
- The unavoidable UDP loss, ordering, and quiet-window limitations remain
  explicit in the RCON operations guide.

## Completed v2.3 Milestone: Reference Production Deployment

This milestone moved GoldSrcOps from a production-oriented container
contract to a continuously running reference environment across a real external
network boundary. The accepted topology and telemetry direction are defined by
Architecture Decisions 16 through 18; the reviewable delivery plan is recorded in
`docs/v2.3-production-deployment.md`.

Milestone status: completed. Architecture and delivery plan are accepted;
immutable GHCR image
publication now covers strict stable and release-candidate tags with
digest-preserving candidate promotion, and the controlled game-server baseline
contract is defined. A self-operated MyArena game VDS is provisioned for the
approved bounded trial, with Timeweb Cloud retained as fallback. DDoS
source-address preservation and bounded trial-period public-address stability
have passed from the production control plane. The other purchase conditions
and checkout approval were completed before provisioning. The pinned ReHLDS
and ReGameDLL_CS versions
have passed both a disposable local Linux rehearsal and the initial controlled
external activation with verified artifacts, A2S, and guarded RCON preflight.
The provider-independent Slice 3
Compose, preflight, and one-shot migration contracts are now defined under
`ops/production`, with only Caddy publishing host ports, PostgreSQL isolated
behind a Unix-domain socket, and the same API image carrying the serialized EF
Core migration bundle. Encrypted off-host backup, repository checking, and
isolated restore-rehearsal automation are implemented and covered by the
container smoke flow. The first host-readiness gate is also implemented as a
read-only Linux audit with deterministic failure-path coverage. A plan-first,
two-phase Ubuntu bootstrap now prepares a dedicated key-only operator before
disabling provider-created SSH access; the audit verifies those effective SSH
settings and rejects public Docker API listeners. Compose validation enforces
runtime restart policies and bounded logs. Snapshot results cannot count as
live host evidence. Signed tag `v2.3.0-rc.5` now identifies the current
immutable candidate from revision `58a74da`; workflow
[#204](https://github.com/tov-vl/gold-src-ops/actions/runs/33631016313)
published and verified digest
`sha256:f146c61e5eba942fc40d27792088ec6666fb2605959429093930c30a43f7d639`.
A private
Backblaze B2 repository in EU Central is initialized, its bucket-scoped
credential and restic recovery key are separated from the control-plane VPS,
and a repository integrity check configured with a `100%` data subset has
passed. The first encrypted PostgreSQL backup, repeated full data check, and
isolated restore rehearsal have now also passed on the target; all eight
migrations and required tables were verified.
The control-plane VPS is provisioned and has passed the two-phase SSH hardening,
controlled reboot, and live baseline host audit. Public DNS for
`api.goldsrcops.com` and real ACME certificate issuance through digest-pinned
Caddy have also passed. The external Auth0 issuer, API audience, namespaced role
claim, exact Reader/Operator roles, and dedicated Operator login are configured;
token issuance has been verified without storing credentials in Git. The
`v2.3.0-rc.5` runtime is enabled behind Caddy: public liveness and readiness are
healthy. The complete Operator, Reader, and negative-token authorization matrix
passed on `v2.3.0-rc.4`; the rc.5 rollout preserved those contracts. The live
runtime host audit confirms only Caddy is publicly published. A guarded daily
backup schedule, scoped retention policy, and freshness probe are active on the
target. Its non-destructive preview retained the existing recoverable snapshot,
and the first completed cycle produced a fresh owner-only marker. The previous
rc.4 source,
environment, and image digest remain available for rollback. Game-server host
access is provisioned with a dedicated key-only operator, disabled root and
interactive SSH authentication, exact-source UFW rules, and synchronized time.
The plan-first game-host bootstrap has now been applied from a reviewed revision:
full dependency-bearing security updates, minimal 32-bit runtime dependencies,
the locked service identity, owner-scoped directories, unattended updates, and
kernel hardening are active. A controlled reboot and post-reboot audit verified
the new kernel, marker, SSH and firewall policy, clock, package state, and
systemd health. The pinned runtime was subsequently installed and independently
verified while its service remained disabled and inactive. Rollback-safe
activation has now applied the reviewed public/private configuration and left
the healthy service active but disabled across reboot. The MyArena game
protection profile is scoped to the reviewed Counter-Strike 1.6 UDP endpoint.
External A2S from the production control plane passed with the expected source
address preserved in both directions. Authenticated `rcon_users` returned a
non-empty exact-source allowlist with no world allowance, and the escrowed
secret matched both protected runtime copies without appearing in process
arguments, environment, unit configuration, public configuration, markers, or
the current journal. A short stability gate completed seven of seven A2S
queries with zero service restarts and zero bots. Trial-period address
stability later passed its bounded terminal comparison. The reviewed endpoint
is now registered in the production control plane, where three scheduled A2S
snapshots over 120 seconds succeeded with zero failures and zero bots. One guarded
production `say` completed with a persisted Operator identity, `Succeeded`
terminal state, complete timestamps, and no failure reason. PostgreSQL and
ReHLDS journal checks confirmed the single dispatch, and the service remained
active with zero restarts. On 2026-09-02, controlled stop, deliberate
configuration withdrawal, atomic restore, post-restart `rcon_users`, and a
30-minute scheduled A2S check also passed. One probe three seconds after start
recorded the startup transition; from the first healthy result, 31 snapshots
spanned 1,802 seconds with zero failures and zero bots while the invocation and
process remained unchanged. The standalone 24-hour no-bot gate was removed from
Slice 2 by an accepted scope decision; bot count remains part of the integrated
24-hour deployment soak. The private observability stack is now deployed on the
target from `v2.3.0-rc.5`. Collector, Prometheus, and Grafana health, live
application and ASP.NET Core metrics, Grafana provisioning, internal-only
networking, absent host port publication, zero restart counts, and root-only
evidence all passed. The broader recovery exercise has now also passed: one
incident opened after the configured threshold and closed after recovery, its
two alert events were delivered once each through a temporary restricted HTTPS
receiver, pending work survived an API restart, and rc.4 rollback plus rc.5
roll-forward preserved durable counts. Trial-period address stability passed.
The 24-hour release soak completed after 24.095 hours; the control-plane
evaluator passed 18/18 checks and the game-host evaluator passed 8/8 checks
with target evidence. Sanitized terminal SLI values are recorded, while raw
target evidence remains outside Git. Final evidence pull request
[#68](https://github.com/tov-vl/gold-src-ops/pull/68) and publication hardening
pull request [#69](https://github.com/tov-vl/gold-src-ops/pull/69) were integrated
through protected `main`; post-merge workflows
[#33784710906](https://github.com/tov-vl/gold-src-ops/actions/runs/33784710906)
and
[#33787856532](https://github.com/tov-vl/gold-src-ops/actions/runs/33787856532)
passed. Signed tag `v2.3.0` promotes the verified candidate digest, final
publication workflow
[#33788658773](https://github.com/tov-vl/gold-src-ops/actions/runs/33788658773)
passed all four jobs, and the stable
[GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.3.0)
is published.

Why this work was selected:

- At selection time, the released backend already demonstrated protocol
  integration, durable workflows, security, observability, and recovery, but
  had not yet been operated as a persistent public environment.
- A remote game server and a separate control-plane host exercise the real
  A2S/RCON network, identity, TLS, secret, backup, and rollback boundaries that
  local and CI environments cannot prove.
- Stable OTLP metrics through an OpenTelemetry Collector make observability an
  explicit deployment component while preserving the existing authenticated
  `/metrics` contract during the v2 compatibility window.
- Real operational data should guide the later public dashboard and Operator UI
  instead of designing those views around synthetic assumptions.

Delivered reviewable slices:

The canonical ten-step execution order for the milestone is in
`docs/v2.3-production-deployment.md#delivery-order-and-status`; this backlog
tracks state without duplicating that operational sequence.

1. Completed: record the provider-independent production topology, OTLP
   transition, threat boundaries, delivery sequence, and evidence contract.
2. Completed: publish an immutable application image from a verified stable or
   release-candidate revision and deploy it by registry digest with a documented
   rollback digest. A matching stable tag promotes the verified candidate digest
   without rebuilding it. Signed tag `v2.3.0-rc.5` and workflow
   [#204](https://github.com/tov-vl/gold-src-ops/actions/runs/33631016313)
   published and verified
   `sha256:f146c61e5eba942fc40d27792088ec6666fb2605959429093930c30a43f7d639`
   from revision `58a74da`. Signed stable tag `v2.3.0` targets that revision;
   workflow
   [#33788658773](https://github.com/tov-vl/gold-src-ops/actions/runs/33788658773)
   promoted and verified the same digest before publishing the stable release.
3. Completed: use `docs/v2.3-controlled-gameserver-baseline.md` to operate
   one controlled ReHLDS and ReGameDLL_CS server outside the control-plane
   host. The local target-runtime compatibility gate is complete. The
   conditional provider decision and bounded trial exception are recorded in
   `docs/v2.3-gameserver-provider-decision.md`; non-deferred pre-purchase checks,
   checkout approval, initial provisioning, key-only operator access, SSH
   hardening, and exact-source UFW are complete. The provider-independent
   foundation under `ops/gameserver` has been applied, followed by a controlled
   reboot and successful post-reboot host audit. Initial management reachability
   survived the reboot. The plan-first pinned runtime installer was matched by
   SHA-256 and applied. Pinned artifact hashes and detached signatures passed;
   HLDS build `5433925`, ReHLDS `3.15.0.896`, ReGameDLL_CS `5.30.0.814`, and all
   recorded hashes were independently verified. The constrained unit was then
   activated through the plan-first workflow, which verified the installed
   runtime and exact SSH/UFW source, accepted the existing secret through stdin
   only, retained rollback on failure, and never enabled the unit. The healthy
   service is active and disabled. Provider game protection, external A2S from
   the production control plane, source-address preservation, a non-empty exact
   `rcon_users` allowlist, secret containment, and a seven-query stability gate
   have passed. The reviewed endpoint is registered in production, and three
   scheduled A2S snapshots over 120 seconds passed with zero failures and zero
   bots. One guarded production `say` passed with its Operator audit identity
   and terminal state persisted. Controlled stop, configuration restore,
   post-restart allowlist verification, and 31 healthy snapshots across 30
   minutes and 2 seconds have also passed with zero bots and a stable process.
   Trial-period address stability and the integrated Slice 6 24-hour no-bot
   observation passed. The standalone 24-hour no-bot observation is
   intentionally omitted; the integrated soak retains the combined stability,
   bot-count, API, durable-state, backup, and observability evidence.
4. Completed: deploy the single-node reference control plane with PostgreSQL,
   TLS reverse proxy, external OIDC integration, secret injection, serialized
   migrations, and off-host backup and restore evidence. The provider-independent
   Compose topology, bounded forwarded-header trust, file-based secret boundary,
   contract preflight, same-image one-shot migration bundle, client-side
   encrypted backup, repository check, and isolated restore rehearsal are
   implemented. The read-only host audit covers Docker service startup, time,
   capacity, UFW, effective SSH hardening, listeners, container port
   publication, and optional external dependencies without recording sensitive
   values. The two-phase Ubuntu bootstrap is plan-only by default and preserves
   the provider-created login until a separate operator session is verified.
   The selected VPS has completed the two-phase SSH hardening, controlled reboot,
   and live baseline audit. DNS for `api.goldsrcops.com` resolves to the host,
   and real ACME certificate issuance has passed with Caddy `2.11.4` pinned by
   digest. A provider-independent JWT role-claim contract and its startup,
   token-validation, Compose, and preflight checks are implemented. The first
   encrypted live backup, repeated full data check, isolated restore rehearsal,
   all eight migrations, idempotent rerun, public runtime HTTPS checks, anonymous
   rejection, Operator authorization, and live runtime host audit have passed.
   The API, Caddy, and PostgreSQL showed zero restarts with the expected
   `unless-stopped` policy and bounded local logs. The guarded daily backup
   schedule, scoped retention policy, mandatory preview, and 36-hour freshness
   probe are active on the target. The preview retained the existing recoverable
   snapshot, the persistent timer is enabled, and its first completed cycle
   produced a valid owner-only freshness marker. The rc.5 rollout passed
   preflight and its already-up-to-date migration bundle before recreating the
   API. The dedicated Reader-only token, missing-role, expired-token,
   wrong-issuer, and wrong-audience cases all passed through public HTTPS. The
   previous rc.4 source, environment file, and digest remain the rollback
   baseline and passed the later live rollback exercise.
5. Completed: the stable OTLP exporter, private digest-pinned OpenTelemetry
   Collector, Prometheus, Grafana, provisioned dashboard, and container-level
   health and metric-path coverage are implemented and deployed. Direct
   `/metrics` remains available for compatibility but is not part of the
   production scrape path. On 2026-09-02, private-network and host-port checks,
   service health, application and ASP.NET Core queries, Grafana datasource and
   dashboard provisioning, zero restart counts, and root-only target evidence
   all passed. Analytics reporting and both Grafana update-check paths are
   disabled; the final Grafana-only recreation did not interrupt other runtime
   services.
6. Completed: the controlled endpoint is registered, repeated scheduled A2S
   polling is verified, and one guarded production `say` has passed with durable
   audit evidence. Controlled failure/recovery, alert delivery, restart with
   pending work, backup/restore, and image rollback have sanitized target
   evidence. The 24-hour deployment soak completed after 24.095 hours with
   18/18 control-plane and 8/8 game-host checks passing, target evidence on both
   hosts, zero failed A2S polls, zero bots, unchanged runtime processes, and a
   passing bounded address comparison. Read-only evaluators, deterministic
   pass/fail smoke coverage, the explicit SLI measurement contract, reviewed
   inactive objective register, terminal observation in
   `docs/service-level-objectives.md`, and `docs/v2.3-readiness.md` are complete.
   The short soak is operational evidence for this reference deployment, not
   proof of long-term reliability or an achieved SLO.

Definition of done:

- The deployed application and every supporting image are pinned by immutable
  version or digest; deployment metadata identifies the source revision.
- Public HTTP traffic terminates at HTTPS, production bearer tokens come from an
  external identity provider, and no production credential is stored in Git,
  image layers, logs, or public telemetry.
- A2S polling and one guarded `say` RCON command succeed against the controlled
  remote server. RCON uses an approved private path or, when the selected host
  cannot provide one, a source-IP allowlist with the residual lack of transport
  confidentiality explicitly accepted.
- YaPB load, when enabled, is visibly separated from real-player counts and is
  never presented as organic usage.
- Production metrics travel over a private OTLP path through the Collector and
  can be queried in Prometheus and Grafana without exposing Collector receivers
  publicly.
- Stopping and restoring the game server opens and closes an availability
  incident, and the configured alert path records the expected durable state.
- PostgreSQL backup restoration, application restart recovery, and image
  rollback are rehearsed and recorded without automatic RCON replay.
- The deployment completes a documented soak period with initial service-level
  indicators and known single-node limitations.

Non-goals for v2.3:

- High availability, Kubernetes, multi-region deployment, or zero-downtime
  database failover.
- A public dashboard or authenticated operator web UI.
- Automatic provisioning or lifecycle management through a hosting-provider
  control-panel API.
- AMX Mod X/ReAPI agent ingestion, durable gameplay inboxes, VIP entitlements,
  or payment processing.
- Service extraction, a message broker, or multiple active polling workers.

## Completed v2.4 Milestone: Continuous Evidence And Operator Experience

v2.4 turns the bounded v2.3 deployment evidence into continuously reviewable
operations data while the operator-facing web experience advances in parallel.
The milestone does not change the v2.3 single-node availability claim or
activate an SLO retroactively.

Delivery order:

1. **External availability contract (completed 2026-09-04)**: define the
   independent primary probe, expected-minute population, raw result schema,
   missing-data rules, retention, and activation gates. Decision 19 and
   `docs/v2.4-external-availability-monitoring.md` define this slice.
2. **Provider selection (completed 2026-09-04)**: Decision 20 and
   `docs/v2.4-synthetic-monitoring-provider-decision.md` conditionally select
   Grafana Cloud Synthetic Monitoring for shadow validation, with explicit
   account, export, retention, cost, credential, and exit gates.
3. **Measurement rollout (implementation slice completed 2026-09-05)**: the
   public readiness primary probe and both diagnostic checks were configured on
   2026-09-04 under
   `v2-4-shadow-001`. The standalone Metrics API exporter, canonical create-only
   JSONL format, overlap deduplication, expected-slot evaluator, and deterministic
   contract fixtures are implemented. A bounded least-privilege live export
   passed on 2026-09-04. A separate private B2 archive, split prefix-scoped
   reader/writer credentials, one content-addressed upload, sequential duplicate
   suppression, SHA-256 verified recovery, and deterministic report comparison
   also passed live on 2026-09-04. The fail-closed GitHub-hosted scheduler,
   pinned-revision gate, serialized writer, bounded catch-up window, temporary
   evidence cleanup, and contract smoke are implemented. Its environment,
   pinned revision, scheduler-enable variable, and one explicitly confirmed
   manual archive/recovery cycle are complete. A scheduled export/archive passed
   in [run 33964685839](https://github.com/tov-vl/gold-src-ops/actions/runs/33964685839),
   and independent read-only recovery of that segment passed digest,
   canonical-record, and deterministic-report checks in
   [run 33968872991](https://github.com/tov-vl/gold-src-ops/actions/runs/33968872991).
   A bounded optional Loki adapter classifies known DNS, connect, TLS,
   and timeout failures, while unknown or ambiguous details remain
   `monitor_error`; safe fixtures cover correlation, response limits, and
   payload-free exceptions. The workflow maps the three optional Loki secrets
   only into the export step. Its least-privilege live `logs:read` path and
   controlled DNS classification passed in
   [run 33964036998](https://github.com/tov-vl/gold-src-ops/actions/runs/33964036998)
   without B2 access or retained raw evidence. The current availability
   implementation slice is now closed. The isolated custom alert-route exercise
   passed on 2026-09-09: the route delivered firing and resolved notifications,
   the final three-bad/two-good hysteresis policy was observed, and the
   temporary validation check was restored disabled. Shadow collection
   continues while one fresh contiguous 24-hour activation window remains
   pending; that gate blocks `API-01` activation, not UI development.
   The first complete audit in
   [run 34257679406](https://github.com/tov-vl/gold-src-ops/actions/runs/34257679406)
   evaluated all 1,440 mature slots but failed closed on one missing slot. Its
   99.93056% aggregate met the draft percentage target, while bounded read-only
   diagnosis found a check-specific cadence delay crossing a UTC-minute
   boundary and no corresponding diagnostic-check interruption.
   A second independent 24-hour audit in
   [run 34340321910](https://github.com/tov-vl/gold-src-ops/actions/runs/34340321910)
   produced the same aggregate and again failed closed on one missing primary
   minute. This time the cadence gap overlapped the diagnostic check in the same
   public location, while a second public location continued reporting
   successful health. Decision 22 resolves the contract mismatch without hiding
   either observation: a mature missing slot remains bad but no longer makes an
   otherwise complete 1,440-slot population invalid. The audit now fails
   separately on evidence integrity and shadow-target attainment. Historical
   runs retain their original failed status. Alert-route proof is complete. The
   fresh revised-policy audit passed in
   [run 34495816614](https://github.com/tov-vl/gold-src-ops/actions/runs/34495816614)
   with 1,440/1,440 good slots, matching identity and population integrity, and
   no bad, missing, pending, duplicate, or ignored non-canonical records.
   The sanitized setup and implementation records are in
   `docs/v2.4-synthetic-monitoring-rollout.md` and
   `docs/v2.4-availability-evidence-exporter.md`.
4. **Activation (recorded 2026-09-10)**: the immutable tuple started the
   prospective seven-day `API-01` window at `2026-09-10T16:45:00Z`, under
   Decision 23, without importing shadow or v2.3 soak samples. The first
   terminal review is permitted after `2026-09-17T16:50:00Z`.
5. **Public experience (completed 2026-09-08)**: the
   compact Blazor Web App and anonymous, cached, deliberately sanitized
   current-status read model are implemented. Pull request #84 packaged the API
   and Web host as separately digest-addressed release artifacts and added the
   hardened Web health and Compose/Caddy contract. Pull request #85 corrected
   the static-SSR rendering contract. Signed candidate `v2.4.0-rc.2` and
   [workflow 33983719134](https://github.com/tov-vl/gold-src-ops/actions/runs/33983719134)
   published and verified both images from revision `c173c27`; the production
   rollout and sanitized post-deployment checks passed. The fresh ten-case
   authenticated API policy matrix passed against the public production route
   on 2026-09-06. A subsequent repository slice added a cached anonymous A2S
   history projection and static-SSR chart: 24 hourly buckets or 28 six-hour
   buckets, explicit unknown gaps, aggregate-only percentages, strict query
   validation, and no server or provider identifiers. Unit, API, real
   PostgreSQL, client, and rendered-page coverage protect the boundary.
   Pull request [#98](https://github.com/tov-vl/gold-src-ops/pull/98)
   corrected the PostgreSQL aggregate precision boundary. Signed candidate
   `v2.4.0-rc.8` passed post-merge
   [workflow 34200721648](https://github.com/tov-vl/gold-src-ops/actions/runs/34200721648)
   and publication
   [workflow 34201594544](https://github.com/tov-vl/gold-src-ops/actions/runs/34201594544).
   The production rollout, exact-digest verification, public health, 24-hour
   and seven-day history rendering, durable-state checks, and rollback
   preservation all passed on 2026-09-08.
   See `docs/v2.4-public-dashboard-deployment.md` and
   `docs/v2.4-reader-portal.md`.
6. **Operator experience (fourth production slice verified 2026-09-07)**: pull
   request #87 added OIDC login/logout, a server-side BFF session, server
   inventory, and current server status without mutations. Signed candidate
   `v2.4.0-rc.3` and
   [workflow 34026555320](https://github.com/tov-vl/gold-src-ops/actions/runs/34026555320)
   published and verified both images from revision `cb4bf4f`. Production OIDC
   callbacks, the namespaced role claim in both token types, Reader and
   Operator rendering, no-role denial, logout, and process-local session
   invalidation across a controlled Web restart passed live verification. The
   fresh invalid-token and role API matrix also passed. A dedicated
   Kestrel-backed Playwright check now verifies empty browser storage,
   token-free protected responses and DOM content, and the opaque secure
   session-cookie contract. This closes the bounded token-boundary evidence
   item; separate availability and release-readiness criteria remain.
   The second repository slice adds an open-incident fleet view and per-server
   tabs for current status versus bounded A2S and incident history. The incident
   API now validates an optional limit, clamps it defensively in the application
   layer, and applies it before EF Core materialization. Reader, Operator,
   no-role, API-boundary, browser token-boundary, and stylesheet-activation
   checks cover the new surface; desktop and mobile screenshots were reviewed
   locally. Pull request
   [#91](https://github.com/tov-vl/gold-src-ops/pull/91) integrated the slice as
   revision `b275115`. Signed candidate `v2.4.0-rc.4` and
   [workflow 34042350507](https://github.com/tov-vl/gold-src-ops/actions/runs/34042350507)
   passed all repository gates plus API and Web image publication and digest
   verification. Production preflight, backup, the already-up-to-date migration
   bundle, rollout, public health, and rollback preservation passed. Reader and
   Operator rendered the server list, open incidents, current server detail,
   and bounded observation and incident history. Runtime continuity, the online
   zero-bot server state, empty incident and durable-work queues, and scheduled
   backup freshness remained healthy.
   The third read-only repository slice adds bounded per-server command audit
   history and cursor-based dead-letter list and detail views. It deliberately
   omits command and event payloads and exposes no mutation controls. Reader,
   Operator, no-role, not-found, API-client, browser token-boundary, and
   responsive desktop/mobile checks cover the surface. Pull request
   [#93](https://github.com/tov-vl/gold-src-ops/pull/93) integrated the slice as
   revision `da3268a`. Signed candidate `v2.4.0-rc.5` and
   [workflow 34101231881](https://github.com/tov-vl/gold-src-ops/actions/runs/34101231881)
   passed all repository gates plus API and Web image publication and digest
   verification. A fresh encrypted off-host backup and full repository check,
   production preflight, the already-up-to-date migration bundle, isolated API
   and Web recreation, public health, and rollback preservation passed on
   2026-09-07. The live command-history and empty dead-letter routes rendered
   without payloads, token-shaped content, or mutation controls. Runtime and
   game-host continuity, the online zero-bot server state, empty incident and
   durable-work queues, and scheduled backup freshness remained healthy.
   The fourth repository slice adds the first guarded mutation surface. An
   Operator can queue only a `say` command through a dedicated static-SSR form;
   Reader sessions receive no form, and the POST independently requires the
   Operator policy plus a stable subject. Antiforgery validation, an explicit
   acknowledgement, and a bounded one-time confirmation tied to subject and
   server reduce accidental or duplicate browser submissions. The confirmation
   is consumed before the API call, no message is retained in it, and an
   uncertain API outcome redirects to command history without retrying. This is
   deliberately not API-level idempotency: RCON still cannot prove whether an
   uncertain command executed. Raw, restart, map-change, and dead-letter replay
   controls remain later Operator work and must preserve the existing API
   policies, concurrency protection, non-retry semantics, and audit trail.
   Pull request [#95](https://github.com/tov-vl/gold-src-ops/pull/95)
   integrated this fourth slice as revision `0635052`. Signed candidate
   `v2.4.0-rc.6` and
   [workflow 34119583632](https://github.com/tov-vl/gold-src-ops/actions/runs/34119583632)
   passed all repository gates plus API and Web image publication and digest
   verification. A fresh encrypted off-host backup and full repository check,
   production preflight, the already-up-to-date migration bundle, isolated API
   and Web recreation, public health, and owner-only `v2.4.0-rc.5` rollback
   preservation passed. Reader concealment and the responsive Operator form
   boundary passed live verification without submitting a production command.
   Runtime and game-host continuity, the online zero-bot server state, empty
   incident and durable-work queues, and scheduled backup freshness remained
   healthy.
   The fifth repository slice adds a guarded dead-letter replay surface. An
   Operator can submit an individual replay with antiforgery validation,
   explicit acknowledgement, and a bounded one-time confirmation tied to the
   subject, event, and canonical idempotency request ID. The confirmation is
   consumed before the API call; an unknown delivery outcome is shown without
   automatic retry. Reader sessions can inspect the durable replay receipt but
   receive no mutation form, and raw event payloads remain outside the browser
   boundary. Local verification passed the solution build, all 450 non-browser
   tests, and four responsive browser tests. Pull request
   [#100](https://github.com/tov-vl/gold-src-ops/pull/100) integrated the slice as
   revision `2769d89`. Signed candidate `v2.4.0-rc.9` and
   [workflow 34235947471](https://github.com/tov-vl/gold-src-ops/actions/runs/34235947471)
   passed all repository gates plus API and Web image publication and digest
   verification. A fresh encrypted off-host backup and full repository check,
   the migration gate, isolated API and Web recreation, public health, and
   rollback preservation passed on 2026-09-08. The live Operator dead-letter
   route rendered its empty state, while anonymous API and Web requests failed
   closed through the Bearer and OIDC boundaries. No production dead letter was
   manufactured and no replay was submitted. Runtime and game-host continuity,
   the online zero-bot server state, empty incident and durable-work queues, and
   backup freshness remained healthy. A follow-up host-preflight correction now
   accepts any-source SSH only on an explicitly declared private management
   interface and still rejects the same rule on any other interface; the
   corrected read-only target audit passed with owner-only evidence.
7. **Stable release (published 2026-09-11)**: pull request
   [#114](https://github.com/tov-vl/gold-src-ops/pull/114) recorded the immutable
   activation tuple, and post-merge
   [workflow 34501694808](https://github.com/tov-vl/gold-src-ops/actions/runs/34501694808)
   passed. Signed annotated tag
   [`v2.4.0`](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.4.0)
   points to revision `2769d89`. Stable
   [workflow 34504124988](https://github.com/tov-vl/gold-src-ops/actions/runs/34504124988)
   promoted the exact API and Web digests verified for `v2.4.0-rc.9` on
   2026-09-10, skipped both rebuild steps, and passed both published-digest
   smoke tests. The GitHub Release was published on 2026-09-11.
8. **First SLO review**: after the complete forward-looking seven-day window,
   publish the reproducible sanitized result and report `API-01` as met or
   missed. Review the target without changing it to fit the observed result.

Acceptance boundaries:

- Exactly one external monitor location defines the `API-01` primary result;
  all other probes remain diagnostic until a separate aggregation decision.
- Raw monitoring results and provider credentials remain outside the production
  control plane and Git; only sanitized aggregates may be public.
- Missing primary samples and planned maintenance consume the error budget.
- No objective is active until its activation tuple is recorded, and no
  objective is achieved before its complete prospective window passes.
- The public UI must not expose server addresses, provider identifiers,
  authenticated operational data, or private observability endpoints.

Non-goals for the first v2.4 slices:

- Multi-node or multi-region GoldSrcOps deployment.
- A custom monitoring service when a managed provider satisfies the contract.
- Gameplay-agent ingestion, VIP entitlements, or payment processing.

## Active v2.5 Milestone: Guarded Server Lifecycle UI

The first local v2.5 slice extends the authenticated Operator portal without
changing the backend contract or the v2.4 release boundary:

- An Operator can pause or resume scheduled monitoring for an existing server.
- A Reader can inspect the current monitoring state but receives no lifecycle
  controls.
- Each change requires antiforgery validation, explicit acknowledgement, and a
  bounded one-time confirmation tied to the authenticated subject, server, and
  requested final state.
- An uncertain HTTP outcome is never retried automatically. The browser returns
  to a freshly loaded server status so the operator can reconcile the result.
- Focused client, confirmation-store, authorization, form-workflow, and
  responsive browser coverage protect the surface.

The second local v2.5 slice adds guarded registration of an existing server:

- Only an authenticated Operator can discover, review, or submit the form.
- The review stores validated connection details in a bounded server-side
  confirmation tied to the authenticated subject. The final form carries only
  antiforgery state, the one-time confirmation, and explicit acknowledgement.
- The API creates the aggregate atomically in `Paused` state, so polling and
  incident evaluation cannot begin between registration and a later lifecycle
  decision. Enabling monitoring remains a separate confirmed action.
- The registration sends a UUID `Idempotency-Key`. PostgreSQL persists its
  intent fingerprint behind a unique index, returns the same server for the
  same request, and rejects reuse for different values.
- The form accepts an optional RCON port but never accepts or stores an RCON
  credential. Credential setup remains a later, separate workflow.
- An uncertain HTTP outcome is never retried automatically. The Operator is
  returned to the inventory to reconcile the endpoint before taking another
  action.

The third local v2.5 slice adds guarded editing of a paused server:

- Reader sessions can inspect connection and polling metadata. Only Operators
  receive the edit form, and only while scheduled monitoring is paused.
- The editable boundary is limited to name, host, query port, optional RCON
  port, poll interval, and notes. RCON credentials are never read, rendered,
  replaced, or cleared by this workflow.
- Each review is bound to the authenticated subject, server, complete
  non-secret draft, and displayed server revision. PostgreSQL optimistic
  concurrency and a fresh monitoring-state check reject stale or racing writes.
- Antiforgery validation, explicit acknowledgement, and a bounded one-time
  confirmation protect the final POST. An uncertain HTTP outcome is not
  retried; the page reloads current Reader data for reconciliation.
- API, PostgreSQL concurrency, client, authorization, confirmation-store,
  workflow, and responsive browser tests cover the slice.

The fourth local v2.5 slice adds guarded RCON credential binding and rotation:

- Reader sessions can inspect only whether the credential is configured, its
  revision, and its timestamps. The current alias, secret reference, and raw
  password never cross the API or browser read boundary.
- Only an Operator can submit a validated deployment alias, and only while
  scheduled monitoring is paused and no command is `Pending` or `Running`.
- The review is bound to the authenticated subject, server, alias, displayed
  server revision, and displayed credential revision. The final POST carries
  only antiforgery state, the one-time confirmation, and explicit
  acknowledgement.
- Server and credential revisions provide optimistic concurrency. PostgreSQL
  rejects stale or racing bindings, including concurrent first-time inserts,
  without silently replacing the winner.
- An uncertain HTTP outcome is never retried automatically. The page reloads
  sanitized credential metadata so the Operator can reconcile before making a
  new attempt.
- Domain, API, PostgreSQL concurrency, client, authorization,
  confirmation-store, workflow, and responsive browser tests cover the slice.

The fifth local v2.5 slice adds guarded server restart:

- Reader sessions can inspect restart readiness, including monitoring state,
  sanitized RCON binding state, and the count of incomplete commands. Only an
  Operator receives the submission form.
- The review is available only when an RCON binding exists and no retained
  command is `Pending` or `Running`. The Web host refreshes the incomplete
  command snapshot again immediately before forwarding the mutation.
- Antiforgery validation, explicit acknowledgement, and a bounded one-time
  confirmation tied to the subject, server, and `Restart` action prevent
  ordinary double submission and cross-command token reuse.
- The existing API queues only the fixed restart command. No raw command or
  browser-provided RCON payload crosses this workflow, and PostgreSQL continues
  to serialize actual command execution per server.
- An uncertain HTTP or RCON outcome is never retried automatically. The
  Operator returns to durable command history and current server status before
  deciding whether a deliberate follow-up is safe.
- Client, confirmation-store, authorization, submit-time precondition,
  workflow, token-boundary, and responsive browser tests cover the slice.

The sixth local v2.5 slice adds guarded map change:

- Reader sessions can inspect sanitized readiness and expected impact. Only an
  Operator receives the map-name form and confirmation controls.
- The API and Web form share a strict map-name allowlist: ASCII letters,
  digits, underscores, and hyphens only, with an alphanumeric first and last
  character. Command separators, embedded whitespace, paths, and control
  characters are rejected before queueing; the Web form normalizes only
  surrounding whitespace.
- The Operator first enters a map name, then reviews a server-side draft. The
  final POST carries only a bounded one-time token and explicit acknowledgement;
  the map cannot be replaced through a forged confirmation field.
- The confirmation is bound to subject, server, and normalized map, consumed
  before the API call, and followed by a fresh incomplete-command check.
- PostgreSQL remains the execution serialization boundary. The Web host never
  retries an uncertain API or RCON outcome and directs the Operator to durable
  command history and current status before a deliberate follow-up.
- API, client, confirmation-store, authorization, forged-form, workflow,
  token-boundary, and responsive browser tests cover the slice.

Raw command controls remain outside these first six slices. No v2.5 UI change
was deployed before the v2.4 stable-publication gate completed. The exact
`v2.5.0-rc.1` candidate is production-deployed and has passed bounded
acceptance; no stable v2.5 release has been published.

Release-candidate preparation and publication completed on 2026-09-11 after
that gate passed. Signed candidate `v2.5.0-rc.1` was published from revision
`c8000c090cb0de03f848ec4e999bcf4505332b93`; [workflow
#34586962203](https://github.com/tov-vl/gold-src-ops/actions/runs/34586962203)
passed `Quality Gate`, `Container Smoke`, `Browser Smoke`, and independent API
and Web published-digest verification. The candidate was subsequently deployed
using those exact immutable digests.

The scope remained frozen to these six slices while bounded production
acceptance ran. On 2026-09-11, a fresh encrypted off-host PostgreSQL
backup, authenticated `100%` repository data check, and network-isolated
candidate restore passed. The exact candidate migration bundle produced 11 EF
Core migration records, all 8 required application tables, and the expected
single controlled-server record. Reapplying the candidate migration bundle
left all 11 migration records unchanged, and the retained previous v2.4 API
passed liveness and readiness against the same migrated copy through a
read-only database connection with background work disabled. The combined
rehearsal completed in approximately 18 seconds; this is an observation, not
an RTO commitment. Matching owner-only evidence remains outside Git, and all
temporary recovery resources were removed.

The compatibility tooling merged in [pull request
#119](https://github.com/tov-vl/gold-src-ops/pull/119) and passed its
[post-merge workflow
#34599354618](https://github.com/tov-vl/gold-src-ops/actions/runs/34599354618).
Production preflight then passed against the exact recorded candidate API and
Web digests without deploying them. The production database and runtime stayed
unchanged, all seven container restart counts remained zero, and public health
returned HTTP `200`.

The digest-pinned rollout and Stage A read-only acceptance then passed on
2026-09-11. The serialized candidate migration completed, only API and Web were
recreated, and the other five control-plane services plus the game host kept
their recorded identities. All seven containers and the game service remained
at zero restarts. Public health, anonymous boundaries, exact candidate
metadata, rollback-reference retention, A2S reachability, the online zero-bot
state, no open incidents or pending durable work, and backup freshness passed
the strict post-rollout checks.

The ten-case live OIDC matrix passed Reader, Operator, missing-role,
expired-token, issuer, and audience boundaries. Reader mutation controls were
concealed, while every v2.5 Operator review flow rendered without final
submission. The post-review durable-state projection exactly matched its
baseline, and the temporary test-only role and token-lifetime settings were
restored and re-read. Sanitized owner-only evidence remains outside Git.

Stage B reversible-state acceptance passed on 2026-09-12. A confirmed pause
advanced the server revision and held both the poll timestamp and snapshot count
unchanged for more than three intervals. The independently available deployment
alias was rebound once with the expected revisions, without changing endpoint
metadata, and a fresh confirmed resume was followed by four reachable
zero-player/zero-bot polls. The rebind used an authenticated Operator API
fallback; the deferred live Web submission and narrowed evidence claim are
recorded explicitly in `docs/v2.5-readiness.md`. Stage B added no incident,
outbox, command, or dead-letter record, and both hosts retained their recorded
process and restart continuity.

A brief incident opened and recovered between Stage A and Stage B. Its two
outbox records were deliberately dispositioned before Stage C. The first
temporary receiver used a source-IP matcher that rejected the observed public
boundary and moved both records to dead letter after one attempt. No database
row was edited directly and no ambiguous delivery was retried automatically.
After the boundary was replaced with a one-time header-protected HTTPS receiver,
an authenticated Operator replay was submitted exactly once for each record.
Both records reached `Processed`, the replay audit delta was exactly two,
pending and dead-letter counts returned to zero, and alert delivery was restored
to disabled. API and Caddy were temporarily recreated before the Stage C
baseline, while their candidate images remained unchanged.

Stage C disruptive-command acceptance passed on 2026-09-12. One fixed restart
reached `Succeeded` and was followed by three healthy scheduled polls. One
change to an installed alternate map reached `Succeeded` and was followed by
two healthy polls; a separate confirmed command restored the original map and
was followed by two more. Every observed poll remained reachable with zero
players and bots. The final audit found no open incident, pending outbox work,
dead letter, or incomplete command; backup evidence remained fresh. All seven
control-plane containers retained their Stage C identities, images, and zero
restart counts, while the game service retained its restart count, invocation,
main process, and single owned UDP listener. Candidate acceptance is therefore
complete. The next release action is exact-digest stable promotion without a
rebuild. The write path still does not create a disposable server record while
no retirement workflow exists. See `docs/release-notes-v2.5.md` and
`docs/v2.5-readiness.md`.

The active `API-01` window continues independently. Candidate work does not
pause or reset it, and any rollout-time bad or missing primary minute consumes
the unchanged error budget.

For subsequent MVP slices, routine UI-only changes use CI, focused browser
coverage, and one 10-15 minute bounded production smoke. Ordinary backend
changes without schema or infrastructure impact add focused integration checks
but keep the same short live window. A 24-hour soak is reserved for changes to
host or network topology, persistence and recovery, telemetry delivery, or a
major release milestone. Passive health and availability checks should run
automatically; interactive user participation is limited to unavoidable OIDC or
MFA ceremonies and separately approved irreversible or financial actions.

## Current API Scope

Access policies for these endpoints are implemented as defined in
`docs/security.md`.

Servers:

- `POST /api/servers`
- `GET /api/servers`
- `GET /api/servers/{id}`
- `PATCH /api/servers/{id}`
- `POST /api/servers/{id}/enable`
- `POST /api/servers/{id}/disable`

Credentials:

- `PUT /api/servers/{id}/credentials/rcon`
- `GET /api/servers/{id}/credentials`

Monitoring:

- `GET /api/servers/{id}/status`
- `GET /api/servers/{id}/snapshots?from=&to=&limit=`
- `GET /api/dashboard/overview`

Incidents:

- `GET /api/incidents/open`
- `GET /api/servers/{id}/incidents?limit=`
- `GET /api/incidents/{id}`

Commands:

- `POST /api/servers/{id}/commands/change-map`
- `POST /api/servers/{id}/commands/restart`
- `POST /api/servers/{id}/commands/say`
- `POST /api/servers/{id}/commands/raw`
- `GET /api/servers/{id}/commands`
- `GET /api/commands/{commandId}`

Alert delivery:

- `GET /api/alert-delivery/dead-letters`
- `GET /api/alert-delivery/dead-letters/{eventId}`
- `POST /api/alert-delivery/dead-letters/{eventId}/replay`
- `GET /api/alert-delivery/replays/{requestId}`

Health and metrics:

- `GET /health/live`
- `GET /health/ready`
- `GET /metrics`

## Current Core Domain Entities

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

`ServerCredential`:

- `Id`
- `ServerId`
- `Kind`
- `SecretReference` (canonical `rcon-secret://<alias>` value)
- `CreatedAtUtc`
- `UpdatedAtUtc`

`CommandExecution`:

- `Id`
- `ServerId`
- `Type`
- `Status`
- `Payload`
- `RequestedBy` (derived from the authenticated token subject; never supplied by the command request)
- `RequestedAtUtc`
- `StartedAtUtc`
- `CompletedAtUtc`
- `ResultSummary`
- `FailureReason`

Future entity candidates:

- `PlayerSnapshot`
- Versioned gameplay-event and durable-inbox models, only after the later
  AMX Mod X/ReAPI agent boundary has its own accepted design.

Alert delivery state and replay audit are intentionally Infrastructure
persistence models, not future Domain entities.

## Verification Plan

Released automated baseline:

- Unit tests for A2S packet parsing with captured byte arrays.
- Unit tests for state transition rules.
- Integration tests for `POST /api/servers` and `GET /api/servers/{id}/status`.
- Testcontainers for PostgreSQL.
- Fake query client for deterministic polling tests.
- Integration test for incident opening after repeated failures.
- Integration coverage for `PATCH /api/servers/{id}`.
- Integration coverage for enable/disable behavior.
- Unit coverage for command execution and credential domain rules.
- API integration coverage for command queueing and credential metadata.
- PostgreSQL-backed integration coverage for the command and credential schema.
- Unit coverage for secret-reference resolution and GoldSrc RCON protocol/client behavior.
- Unit and API integration coverage for command execution metrics.
- Unit and PostgreSQL integration coverage for background dispatch, atomic per-server claiming, and interrupted-command recovery.
- API integration coverage for anonymous, Reader, and Operator access across the endpoint policy matrix.
- API integration coverage proving command requester identity comes from the authenticated token subject.
- Unit and PostgreSQL integration coverage for snapshot-retention cutoff,
  bounded batching, metrics, and preservation of non-snapshot monitoring data.
- API and PostgreSQL integration coverage for bounded dead-letter inspection,
  audited replay, idempotency, rollback, aggregate ordering, and concurrent
  requests.
- Unit and API integration coverage for replay outcome metrics, Prometheus
  export, HTTP-validation accounting, sanitized lifecycle logs, cancellation,
  and fault redaction.

For v1.1:

- Container-image smoke coverage for the documented deployment shape.

For v2 alert delivery:

- Unit and PostgreSQL integration coverage for transactional enqueueing,
  claiming, ordering, retry, dead-letter, stale-claim recovery, metrics, log
  safety, and bounded retention.
- Synthetic HTTP-server coverage for one POST per attempt, idempotency headers,
  status classification, timeout, `Retry-After`, redirect, and response bounds.
- Production container smoke coverage for HTTPS startup validation, enabled
  dispatcher registration, and endpoint/authorization log safety.

For the completed v2.3 deployment milestone:

- Completed: configuration and fail-open API integration coverage for optional
  OTLP export without changing direct metric names, bounded labels, or API
  readiness semantics.
- Completed: container-level validation of Collector, Prometheus, and Grafana
  configuration, provisioning, the private application-to-Collector metric
  path, and API readiness after Collector loss.
- Completed: deterministic host-preflight coverage for Docker boot enablement,
  time, capacity, firewall scope, prohibited public listeners, published ports,
  and external dependency failures; live output remains separate target
  evidence.
- Completed: target-environment evidence for TLS, OIDC metadata, secret
  injection, external A2S/RCON traffic, backup restoration, and immutable
  rollback.
- Completed: a controlled stop/recovery scenario that verifies incidents and
  durable alert state without treating YaPB sessions as real-player adoption.

## Portfolio Baseline And Remaining Gaps

The released v1 baseline includes:

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

Remaining portfolio gaps, in priority order:

- Complete the active prospective seven-day `API-01` window and publish its
  first reproducible met-or-missed review without changing the target to fit
  the result. Alert routing, archive, independent scheduled-segment recovery,
  the revised-policy shadow audit, and activation are proved; neither the
  shadow nor the completed v2.3 24-hour sample is an achieved SLO claim.
- A concise video walkthrough and a small evidence-based postmortem covering
  the completed controlled failure/recovery exercise.
- A later versioned AMX Mod X/ReAPI event agent with a durable inbox, only after
  the production control plane and UI are established.

VIP entitlements and payment integration remain a separate, later milestone.
The first entitlement experiment must stay sandbox-only and must not process
real money.
