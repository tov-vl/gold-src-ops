# GoldSrcOps v2.19.0 Release Notes

Status as of 2026-09-20: `v2.19.0-rc.3` candidate acceptance passed; stable
promotion remains a separate decision. The exact `rc.3` receiver image is
deployed in `CatchUp` mode with provider delivery disabled. The preserved
recovery was replayed once, processed by one bounded dispatcher, and resolved
the receiver incident without creating provider work. The source sender is
disabled again.

## Overview

GoldSrcOps v2.19 adds a durable, provider-independent availability-event
receiver. It accepts the existing bounded incident webhook contract, preserves
event-level idempotency and incident correlation in a dedicated PostgreSQL
database, and owns a separate provider-delivery outbox with bounded retry and
recovery semantics.

The first persistent placement is the existing `gso-control-01` VDS. This is a
cost-conscious MVP with process and data isolation, not a separate failure
domain or a high-availability claim. Production sender delivery and provider
delivery remain disabled during the first CatchUp deployment.

## Included In v2.19

- A separate non-root `GoldSrcOps.AlertReceiver` ASP.NET Core runtime and
  immutable `gold-src-ops-alert-receiver` image.
- Strict bearer authorization, request validation, idempotency keys, and
  durable `202`/duplicate `204` ingestion semantics.
- A dedicated receiver database, migration history, event ledger, incident
  correlation, and provider-delivery outbox.
- A provider dispatcher with lease recovery, bounded exponential retry,
  trigger/resolve ordering, permanent-failure handling, and telemetry.
- Two additive receiver-only EF Core migrations packaged in the receiver image.
- Standalone and control-plane co-location Compose contracts with file-backed
  secrets, non-root execution, read-only filesystems, resource limits, and an
  exact Caddy ingestion route.
- Separate encrypted backup, full repository check, and isolated
  restore/migration rehearsal support for the receiver database.
- Immutable candidate/stable publication and independent digest verification
  for the receiver image alongside the existing API and Web artifacts.
- Recovery correlation that preserves strict incident identity while accepting
  the source incident's nondecreasing final failure count.

## Data And Security Boundary

The receiver stores only the reviewed availability-event projection and its
own provider-delivery state. It does not share the control-plane database,
browser session, RCON credential, game-host identity, or provider secret. The
public route exposes only the exact POST ingestion path; health and metrics
remain private deployment concerns.

Secrets enter through owner-controlled files outside Git. The runtime is UID
`1654`, uses a read-only root filesystem and dropped capabilities, and fails
closed when required database, ingress authorization, or enabled-provider
configuration is missing.

## Compatibility And Rollback

- Existing API, Web, control-plane database, public contracts, Reader and
  Operator roles, game host, and event producer are unchanged.
- The receiver migrations apply only to its dedicated database. Ordinary
  runtime startup does not apply them.
- The first rollout uses `CatchUp` mode with provider delivery disabled. Its
  bounded historical opening was accepted and provider-suppressed; the matching
  recovery was preserved as a source dead letter after a fail-closed `409`.
- The `rc.3` fix changes no schema or public request shape. It accepts a recovery
  failure count equal to or greater than the opening value, stores the final
  value, and continues to reject a lower value without mutation.
- Rollback removes the receiver route and stops the receiver boundary while
  preserving its dedicated database and encrypted backup evidence. Existing
  API and Web digests remain the known-good control-plane boundary.
- Enabling historical catch-up or a live dispatcher requires a separate gate
  after the dormant receiver passes its deployment evidence.

## Acceptance

The repository acceptance boundary includes required CI, independent
published-digest smoke, exact OCI identity, both receiver migrations,
idempotent ingestion across restart, encrypted backup with a `100%` repository
check, and an isolated restore with migration reapplication.

Target acceptance additionally requires a digest-pinned `rc.3` receiver-only
update on `gso-control-01`, exact replay of the preserved recovery through the
guarded operator boundary, one bounded sender dispatch, a resolved receiver
incident with two provider-suppressed events, and continuity of the existing
control plane and game host.

The `rc.2` dormant deployment and backup/restore boundary passed. The signed
`rc.3` revision, required CI, three immutable image digests, receiver-only
rollout, guarded replay, sender disablement, and independent continuity checks
also passed. The accepted receiver state contains exactly two provider-muted
CatchUp events, a resolved incident with final failure count `5`, and zero
provider outbox records. Stable promotion is intentionally not implied by this
candidate evidence.

## Known Limits

- Co-location does not survive loss or maintenance of `gso-control-01`.
- Candidate acceptance does not prove human notification, escalation, sustained
  live delivery, rate-limit behavior, paging reliability, long-term reliability,
  or an achieved SLO.
- Provider delivery remains disabled until a separately reviewed catch-up and
  one-dispatcher canary complete.
- A healthy receiver does not by itself prove the control-plane sender is
  enabled or that provider notifications reach a person.

## References

- [v2.19 release readiness](v2.19-readiness.md)
- [Permanent receiver decision and adapter evidence](v2.19-permanent-receiver-readiness.md)
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [Alert delivery operations](alert-delivery.md)
- [PostgreSQL backup and restore](postgresql-backup.md)
- [Security](security.md)
- [Project backlog](backlog.md)
- [v2.18.0 release notes](release-notes-v2.18.md)
