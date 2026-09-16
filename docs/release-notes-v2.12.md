# GoldSrcOps v2.12.0 Release Notes

Status as of 2026-09-16: stable release published. Signed `v2.12.0` promotes
the exact accepted `v2.12.0-rc.1` API and Web digests without rebuilding either
image.

## Overview

GoldSrcOps v2.12 brings the newest retained anonymous completed-round record
into the existing Reader server overview. Operators can see the latest map,
occurrence time, and aggregate player and bot counts during ordinary triage,
then follow the existing link to bounded Events history.

This is an additive read-only Web release. It composes an existing Reader API
with `limit=1`; it does not add an API contract, database migration, lifecycle
mutation, background worker, identity-provider change, infrastructure change,
or new secret boundary.

## Included In v2.12

- One latest-round summary on the authenticated Reader server overview.
- Existing minimized fields only: occurrence time, map, aggregate players, and
  aggregate bots.
- A direct link to the existing bounded Events history for that server.
- A normal empty-history state when no completed round is retained.
- An isolated unavailable state that leaves core server and A2S status usable
  when the gameplay projection cannot be loaded.
- Static server-side rendering, responsive desktop/mobile layout, and focused
  browser token-boundary coverage.

The signed source also contains the completed v2.11 pilot tooling and evidence.
That bounded pilot delivered one reviewed event and was explicitly rolled back.
The temporary identity, credential, activation, and plugin state was removed;
the dormant agent remains inactive and boot-disabled. v2.12 does not reactivate
or extend that pilot.

## Data And Security Boundary

The Web host requests exactly one record through the existing bounded
game-event Reader endpoint. Source identity, sequence, ingestion metadata,
request body, raw database row, server address, credentials, secret references,
tokens, and RCON content do not cross the browser boundary.

Bearer tokens remain server-side in the Web host. The page adds no form or
mutation control, and anonymous public routes remain unchanged. Failure of the
latest-round request is isolated from the core server and A2S status view.

## Compatibility

- Existing API and Web routes remain available; the overview composition is
  additive.
- No EF Core migration, schema maintenance, data backfill, retention change, or
  new worker is required.
- API and Web are deployed together from verified candidate digests under the
  existing production contract.
- Rollback restores both v2.10 application images and leaves PostgreSQL, Caddy,
  telemetry services, the game host, and dormant pilot state unchanged.

## Candidate Acceptance

Repository acceptance covers populated, empty, and unavailable gameplay
history; isolation from the A2S status; exact `limit=1` request behavior;
responsive rendering; navigation to Events history; and the browser token
boundary. `Change Scope`, `Quality Gate`, `Container Smoke`, and `Browser Smoke`
passed for the product pull request and its post-merge workflow.

Candidate workflow
[#35106025207](https://github.com/tov-vl/gold-src-ops/actions/runs/35106025207)
published and verified both immutable images from revision `93989c0`. Production
retained those exact digests after an API/Web-only rollout. Four healthy samples
over 182 seconds verified public health, release identity, A2S continuity, zero
bots, zero open or newly opened incidents, zero dead letters, zero incomplete
commands, unchanged restart counts, and fresh backup evidence. A separate
692-second game-host check preserved the game service while the game-event agent
remained inactive and boot-disabled.

The authenticated production overview rendered the newest retained round and
its bounded Events history using only the reviewed aggregate fields. No summary
mutation control or browser console error appeared.

The read-only baseline contained two pre-existing pending alert-outbox records
with alert delivery disabled. The count remained exactly two in every sample;
the release did not mutate or drain the queue. This is a recorded operational
follow-up rather than a claim that durable work is empty.

The smoke must not manufacture a gameplay event or projection failure, submit a
mutation, reactivate the producer or delivery path, or change monitoring
cadence. Empty and unavailable behavior may be reused from Browser Smoke on the
exact immutable Web image. It does not repeat backup/restore, lifecycle
commands, RCON traffic, OIDC reconfiguration, or a multi-hour or multi-day soak.
The exact ordered gate and rollback rules are defined in
[v2.12 release readiness](v2.12-readiness.md).

## Stable Publication

Signed stable tag `v2.12.0` targets accepted candidate revision `93989c0`.
Stable workflow
[#35111632801](https://github.com/tov-vl/gold-src-ops/actions/runs/35111632801)
skipped both image build paths, promoted the exact API and Web candidate
digests, and passed both published-image smoke jobs. Independent GHCR inspection
confirmed that the stable references resolve to those same digests. The
[GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.12.0)
is published.

## Known Limits

- The summary shows only the newest retained completed round. It is not a live
  round state, full match history, or retention guarantee.
- Player and bot values are aggregate counts; player identity and per-player
  activity are intentionally absent.
- The retained v2.11 evidence covers one bounded event, not continuous producer
  or delivery operation.
- Two pre-existing pending alert-outbox records remain with delivery disabled.
  They did not grow during candidate acceptance and require separate operational
  disposition.
- Empty and unavailable states are operational UI states, not evidence of
  successful gameplay delivery.
- The reference deployment remains single-node and does not claim high
  availability, multi-region resilience, long-term reliability, or an achieved
  SLO.

## References

- [v2.12 release readiness](v2.12-readiness.md)
- [Latest Round Overview pull request](https://github.com/tov-vl/gold-src-ops/pull/156)
- [Candidate publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35106025207)
- [Stable publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35111632801)
- [GitHub Release v2.12.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.12.0)
- [v2.11 bounded pilot](v2.11-game-event-pilot.md)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.10.0 release notes](release-notes-v2.10.md)
- [Deployment](deployment.md)
- [Security](security.md)
