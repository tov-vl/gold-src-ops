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
  linear history, and the GitHub Actions `Quality Gate`; it also blocks branch
  deletion and force pushes.
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

## Active v2.3 Milestone: Reference Production Deployment

The next milestone moves GoldSrcOps from a production-oriented container
contract to a continuously running reference environment across a real external
network boundary. The accepted topology and telemetry direction are defined by
Architecture Decisions 16 through 18; the reviewable delivery plan is recorded in
`docs/v2.3-production-deployment.md`.

Milestone status: architecture and delivery plan accepted; immutable GHCR image
publication now covers strict stable and release-candidate tags with
digest-preserving candidate promotion, and the controlled game-server baseline
contract is defined. A self-operated MyArena game VDS is provisioned for the
approved bounded trial, with Timeweb Cloud retained as fallback. DDoS
source-address preservation has passed from the production control plane;
public-address stability across the remaining trial is the outstanding
empirical provider gate. The other purchase conditions and checkout approval
completed before provisioning. The pinned ReHLDS and ReGameDLL_CS versions
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
stability remains an empirical provider gate. The reviewed endpoint is now
registered in the production control plane, where three scheduled A2S snapshots
over 120 seconds succeeded with zero failures and zero bots. One guarded
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
roll-forward preserved durable counts. Trial-period address stability remains
pending. The 24-hour release soak is active from `2026-09-02T16:23:57Z` through
`2026-09-03T16:23:57Z`; matching root-only baselines passed on both hosts. Raw
target evidence remains outside Git.

Why this work is selected:

- The released backend already demonstrates protocol integration, durable
  workflows, security, observability, and recovery, but it is not yet operated
  as a persistent public environment.
- A remote game server and a separate control-plane host exercise the real
  A2S/RCON network, identity, TLS, secret, backup, and rollback boundaries that
  local and CI environments cannot prove.
- Stable OTLP metrics through an OpenTelemetry Collector make observability an
  explicit deployment component while preserving the existing authenticated
  `/metrics` contract during the v2 compatibility window.
- Real operational data should guide the later public dashboard and Operator UI
  instead of designing those views around synthetic assumptions.

Planned reviewable slices:

The canonical ten-step execution order for the remaining milestone is in
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
   from revision `58a74da`.
3. In progress: use `docs/v2.3-controlled-gameserver-baseline.md` to operate
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
   Trial-period address stability remains pending. The standalone 24-hour no-bot
   observation is intentionally omitted; the integrated Slice 6 24-hour soak
   retains the combined stability, bot-count, API, durable-state, backup, and
   observability evidence.
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
6. In progress: the controlled endpoint is registered, repeated scheduled A2S
   polling is verified, and one guarded production `say` has passed with durable
   audit evidence. Controlled failure/recovery, alert delivery, restart with
   pending work, backup/restore, and image rollback now have sanitized target
   evidence. The 24-hour deployment soak is running from
   `2026-09-02T16:23:57Z` through `2026-09-03T16:23:57Z` after matching
   fail-closed baselines passed on both hosts. A read-only control-plane
   evaluator, deterministic pass/fail smoke, explicit SLI measurement contract,
   and draft `docs/v2.3-readiness.md` are implemented. Complete the target
   evaluation and separate game-host continuity and trial-period
   address-stability checks, then record terminal SLI observations and propose
   SLO values. The shorter soak is operational evidence for this reference
   deployment, not proof of long-term reliability or an achieved SLO.

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
- `GET /api/servers/{id}/incidents`
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

For the active v2.3 deployment milestone:

- Completed: configuration and fail-open API integration coverage for optional
  OTLP export without changing direct metric names, bounded labels, or API
  readiness semantics.
- Completed: container-level validation of Collector, Prometheus, and Grafana
  configuration, provisioning, the private application-to-Collector metric
  path, and API readiness after Collector loss.
- Deterministic host-preflight coverage for Docker boot enablement, time,
  capacity, firewall scope, prohibited public listeners, published ports, and
  external dependency failures; live output remains separate target evidence.
- Target-environment evidence for TLS, OIDC metadata, secret injection,
  external A2S/RCON traffic, backup restoration, and immutable rollback.
- A controlled stop/recovery scenario that verifies incidents and durable alert
  state without treating YaPB sessions as real-player adoption.

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

- Complete terminal soak evidence for the continuously running reference
  deployment across its real external A2S/RCON boundary; the recovery exercise
  is complete and the 24-hour observation is active.
- A compact public read-only dashboard backed by a deliberately sanitized
  projection.
- An authenticated Reader/Operator web workflow for servers, incidents,
  commands, dead letters, and replay.
- Recorded uptime, initial SLOs, a controlled failure/recovery demonstration, a
  concise video walkthrough, and a small evidence-based postmortem.
- A later versioned AMX Mod X/ReAPI event agent with a durable inbox, only after
  the production control plane and UI are established.

VIP entitlements and payment integration remain a separate, later milestone.
The first entitlement experiment must stay sandbox-only and must not process
real money.
