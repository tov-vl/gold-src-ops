# GoldSrcOps v2.17.0 Release Notes

Status as of 2026-09-17: candidate preparation. The Alert Delivery Overview
product slice is integrated in protected `main`; no v2.17 candidate tag,
production rollout, stable tag, or GitHub Release has been published yet.

## Overview

GoldSrcOps v2.17 gives Readers one deliberately small operational view of the
existing durable alert-delivery outbox. A new aggregate-only API reports the
configured delivery state, pending and processing work, dead-letter count,
oldest pending event time, and an explicit observation time. A new
static-rendered page turns that snapshot into clear, active, paused, and
action-required states and links to the existing bounded dead-letter review.

This is an additive read-only API and Web release. It changes no database
schema, authorization policy, background worker, delivery retry behavior,
production identity, mutation permission, webhook configuration, event
producer, or game-host component.

## Included In v2.17

- Reader-authorized `GET /api/alert-delivery/status`.
- Aggregate pending, processing, and dead-letter counts from the existing
  outbox table.
- The occurrence time of the oldest pending event, or `null` when no pending
  event exists.
- The configured delivery-enabled value and a UTC observation time generated
  by the API.
- A static-rendered `/operator/alert-delivery` page with distinct healthy,
  active, paused, and action-required presentations.
- A navigation link to the existing bounded dead-letter list without adding a
  replay control to the overview.
- Focused service, PostgreSQL translation, API authorization, Reader client,
  static-rendering, responsive Chromium, and browser token-boundary coverage.

## Data And Security Boundary

The status contract contains only one Boolean, three aggregate counts, and two
timestamps. It does not expose event identifiers, event types, aggregate
identities, payloads, retry errors, webhook addresses, authorization values,
database rows, credentials, tokens, server addresses, or RCON content.

The endpoint and page reuse the existing Reader policy. Anonymous requests
remain unauthorized, and the existing Operator-only replay endpoint retains
its separate policy and behavior. Bearer tokens remain server-side in the Web
host. The overview adds no form, command, replay, enable, disable, or queue
mutation control.

## Compatibility And Semantics

- Existing clients and endpoints are unchanged. The new route and response
  contract are additive.
- API and Web should be published and deployed together because the new Web
  page consumes the new route.
- `isEnabled` reports reviewed application configuration. It does not by
  itself prove that a worker is alive, a webhook is reachable, or an alert was
  delivered.
- Counts are a point-in-time database projection grouped by the existing
  outbox status values. They are not a delivery-rate, latency, or SLO claim.
- `oldestPendingAtUtc` is the event occurrence time of the oldest pending row,
  not a queue lease, retry deadline, or guaranteed enqueue timestamp.
- `observedAtUtc` is recorded after the aggregate query completes. It is not a
  transaction boundary shared with a second endpoint or browser render.
- A dead letter takes presentation priority over pending or processing work;
  otherwise active work takes priority over clear or paused state.
- Rollback restores both v2.16 application images while leaving PostgreSQL,
  Caddy, telemetry, the game host, durable outbox rows, and delivery
  configuration unchanged.
- No EF Core migration, data backfill, queue drain, fresh backup, restore
  rehearsal, identity change, or infrastructure rollout is required.

## Repository Acceptance

Pull request [#177](https://github.com/tov-vl/gold-src-ops/pull/177) contains
the aggregate query, Reader API contract, overview page, and focused coverage.
Its pull request workflow
[#35264152171](https://github.com/tov-vl/gold-src-ops/actions/runs/35264152171)
passed `Change Scope`, `Quality Gate`, `Container Smoke`, and `Browser Smoke`.
The product was squash-merged as revision
`6422ba2a16ba88bb2da296b9bf661fcbe6f52c6a`. Its post-merge workflow
[#35265009075](https://github.com/tov-vl/gold-src-ops/actions/runs/35265009075)
passed the same four required checks.

Local product validation passed 523 backend and integration tests, 238 Web
tests, formatting verification, the focused PostgreSQL aggregate-query test,
and the dedicated Alert Delivery Overview Chromium test at desktop and mobile
viewports. Opt-in browser tests skipped by the ordinary Web suite were not
counted as passed; the new dedicated browser test was executed separately.

Release acceptance follows the R1 path in the version-neutral
[release process](release-process.md). The signed candidate tag performs one
complete immutable-image workflow. Production then receives only the exact API
and Web digests and runs a three-sample, three-minute read-only smoke. It does
not repeat a game-host soak, migration or restore rehearsal, Auth0 change,
RCON action, queue replay, delivery activation, or synthetic alert creation.

Candidate identity, rollout evidence, and stable publication remain pending.
The exact ordered gates and rollback rules are defined in
[v2.17 release readiness](v2.17-readiness.md).

## Known Limits

- The overview is a current aggregate snapshot, not a queue history, delivery
  audit, throughput chart, or per-destination health view.
- Configured enabled state does not prove worker liveness or downstream
  webhook reachability.
- The oldest pending time is not an alert-age SLO and does not explain why work
  remains pending.
- The page does not auto-refresh, stream updates, retain historical samples, or
  raise a new incident when counts change.
- A Reader can follow the dead-letter link, but replay remains a distinct
  Operator-authorized workflow with its existing confirmation boundary.
- Production acceptance observes naturally existing queue state. It must not
  enable delivery, manufacture a dead letter, drain pending work, or perform a
  replay merely to exercise every visual state.
- Existing reviewed deferred outbox work remains outside this release. The
  smoke compares it with a fresh baseline and does not reclassify it.
- The reference deployment remains single-node and does not claim high
  availability, multi-region resilience, long-term reliability, or an achieved
  SLO.

## References

- [v2.17 release readiness](v2.17-readiness.md)
- [Alert Delivery Overview pull request](https://github.com/tov-vl/gold-src-ops/pull/177)
- [Product pull request workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35264152171)
- [Product post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35265009075)
- [Release process](release-process.md)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.16.0 release notes](release-notes-v2.16.md)
- [Alert delivery operations](alert-delivery.md)
- [Dead-letter replay design](dead-letter-replay.md)
