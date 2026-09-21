# GoldSrcOps v2.22.0 Release Notes

Status as of 2026-09-21: release-candidate preparation. The product revision is
integrated into protected `main`; candidate publication, target acceptance,
stable promotion, and final release evidence remain pending.

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

Target acceptance remains pending. It requires exact candidate Web and
AlertReceiver digests, receiver-before-Web rollout, three healthy read-only
samples over at least three minutes, an existing Operator session, matching
bounded source and provider evidence, forbidden Reader access, unchanged
public Caddy exposure, unchanged durable queue and review state, receiver mode
`CatchUp`, and both delivery workers remaining disabled.

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
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [Security](security.md)
- [Project backlog](backlog.md)
- [v2.21.0 release notes](release-notes-v2.21.md)

