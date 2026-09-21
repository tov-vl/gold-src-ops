# GoldSrcOps v2.22.0 Release Notes

Status as of 2026-09-22: released. Signed stable tag `v2.22.0` identifies the
exact accepted `v2.22.0-rc.1` revision, and the stable API, Web, and
AlertReceiver references preserve the independently verified candidate
digests.

## Overview

GoldSrcOps v2.22 adds End-to-End Delivery Readiness to the existing Operator
provider-delivery page. An authenticated Operator can inspect the
control-plane sender, receiver mode, and provider worker as one read-only chain
without direct database, receiver, or provider access.

This is an additive read-only Web and AlertReceiver release. It changes no
database schema, delivery worker, retry behavior, public route, authorization
role, identity, provider configuration, receiver mode, event producer, or
game-host component.

## Included In v2.22

- Sanitized `ReceiverMode` on the existing separately authorized receiver
  status response.
- Server-side composition of source alert-delivery and provider-delivery
  snapshots.
- Operator-only three-stage delivery chain for sender, receiver, and provider
  worker.
- Explicit `Safely paused`, `Partially active`, `Action required`, and `Live`
  classifications.
- Fail-closed `Unknown` handling when an older receiver does not return its
  mode.
- Responsive desktop and mobile presentation alongside the existing queue and
  immutable dead-letter review surfaces.
- Explicit read-only copy and no controls for retry, replay, deletion,
  reclassification, unblocking, review submission, worker activation, or
  receiver-mode changes.

## Data And Security Boundary

The composed view exposes only worker configuration, receiver mode, aggregate
queue counts, review workload, and previously reviewed timestamps. It does not
expose provider envelopes, event payloads, raw responses, failure details,
claim identities, destinations, server addresses, credentials, tokens,
authorization values, or database rows.

Both protected snapshots are requested by the Web BFF. Browser code receives
neither receiver authorization nor a new public endpoint. The existing
provider-operations authorization remains disabled by default, production
Caddy exposes no provider-operations route, and Reader and anonymous sessions
cannot open the Operator view.

## Compatibility And Rollback

- Existing databases, public routes, roles, identities, and delivery settings
  are unchanged.
- A v2.21 Web ignores the additive receiver-mode field.
- A v2.22 Web treats a missing mode from a v2.21 receiver as `Unknown` and
  `Partially active` rather than reporting a false healthy or paused state.
- Deploy AlertReceiver before Web to avoid that temporary unknown state.
- No migration, backfill, queue drain, backup/restore rehearsal, worker
  activation, or receiver-mode change is required.
- Rollback restores the accepted v2.21 Web and AlertReceiver digests without a
  database, queue, authorization, API, Caddy, telemetry, identity, mode, or
  game-host action.

## Acceptance

Repository and release evidence is recorded in
[v2.22 release readiness](v2.22-readiness.md). Product PR
[#203](https://github.com/tov-vl/gold-src-ops/pull/203) passed the complete
required CI before squash merge, and post-merge workflow
[#35639230653](https://github.com/tov-vl/gold-src-ops/actions/runs/35639230653)
passed `Change Scope`, `Quality Gate`, `Container Smoke`, and `Browser Smoke`.

Readiness PR [#204](https://github.com/tov-vl/gold-src-ops/pull/204) froze exact
revision `7c4d6f63f756f702693281f1a34c3f0c8315adce`. Candidate
[workflow #35651626318](https://github.com/tov-vl/gold-src-ops/actions/runs/35651626318)
published and independently verified all three workflow images.

R1 target acceptance replaced only AlertReceiver and Web, in that order, with
their candidate digests. API, both PostgreSQL schemas, Caddy, telemetry,
identity configuration, and game-host runtime remained unchanged. Three
healthy samples spanned 181 seconds with exact image identity, zero unexpected
restarts, healthy public and private endpoints, unchanged durable queue and
review state, receiver mode `CatchUp`, and both delivery workers disabled. The
authenticated Operator chain matched bounded owner-only source and provider
evidence and exposed no credential, payload, raw provider response, or
mutation control. Public provider-operations access remained absent. Reader
denial is supported by exact-revision policy and browser tests; no separate
live Reader session was created during target acceptance.

Signed stable tag `v2.22.0` targets the exact accepted revision. Stable
[workflow #35659136810](https://github.com/tov-vl/gold-src-ops/actions/runs/35659136810)
skipped all three image-build steps, promoted the accepted API, Web, and
AlertReceiver digests unchanged, and independently smoke-tested each stable
reference. The [GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.22.0)
is published. Stable promotion changed no production runtime.

No production data or configuration should be changed to make the chain
display a particular state.

## Known Limits

- The chain is a current snapshot, not history, throughput analysis, or an
  event stream.
- The page does not auto-refresh.
- `Live` configuration cannot prove provider reachability or successful
  notification delivery.
- A short production smoke cannot prove long-term reliability, high
  availability, sustained provider operation, or an achieved SLO.
- The receiver remains co-located with the control plane and is not a separate
  failure domain.

## References

- [v2.22 release readiness](v2.22-readiness.md)
- [End-to-End Delivery Readiness pull request](https://github.com/tov-vl/gold-src-ops/pull/203)
- [Product CI](https://github.com/tov-vl/gold-src-ops/actions/runs/35637975342)
- [Product post-merge CI](https://github.com/tov-vl/gold-src-ops/actions/runs/35639230653)
- [Release readiness pull request](https://github.com/tov-vl/gold-src-ops/pull/204)
- [Candidate workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35651626318)
- [Stable workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35659136810)
- [GoldSrcOps v2.22.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.22.0)
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [Security](security.md)
- [Project backlog](backlog.md)
- [v2.21.0 release notes](release-notes-v2.21.md)
