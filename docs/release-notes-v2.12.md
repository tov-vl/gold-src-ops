# GoldSrcOps v2.12.0 Release Notes

Status as of 2026-09-16: release-candidate preparation. The Latest Round
Overview scope is integrated in protected `main`; signed `v2.12.0-rc.1`
publication and its bounded production smoke remain pending.

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

Production acceptance follows the additive read-only row in the risk-based
release policy. One read-only session must collect at least three healthy
post-rollout samples spanning at least three minutes. It verifies public health,
release identity, the latest-round summary, its bounded Events link, browser
secret boundaries, A2S continuity, zero bots, durable-work and incident counts,
unchanged restart counts, backup freshness, and the inactive game-event agent.

The smoke must not manufacture a gameplay event or projection failure, submit a
mutation, reactivate the producer or delivery path, or change monitoring
cadence. Empty and unavailable behavior may be reused from Browser Smoke on the
exact immutable Web image. It does not repeat backup/restore, lifecycle
commands, RCON traffic, OIDC reconfiguration, or a multi-hour or multi-day soak.
The exact ordered gate and rollback rules are defined in
[v2.12 release readiness](v2.12-readiness.md).

## Known Limits

- The summary shows only the newest retained completed round. It is not a live
  round state, full match history, or retention guarantee.
- Player and bot values are aggregate counts; player identity and per-player
  activity are intentionally absent.
- The retained v2.11 evidence covers one bounded event, not continuous producer
  or delivery operation.
- Empty and unavailable states are operational UI states, not evidence of
  successful gameplay delivery.
- The reference deployment remains single-node and does not claim high
  availability, multi-region resilience, long-term reliability, or an achieved
  SLO.

## References

- [v2.12 release readiness](v2.12-readiness.md)
- [Latest Round Overview pull request](https://github.com/tov-vl/gold-src-ops/pull/156)
- [v2.11 bounded pilot](v2.11-game-event-pilot.md)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.10.0 release notes](release-notes-v2.10.md)
- [Deployment](deployment.md)
- [Security](security.md)
