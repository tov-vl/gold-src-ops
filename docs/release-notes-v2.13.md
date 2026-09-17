# GoldSrcOps v2.13.0 Release Notes

Status as of 2026-09-16: stable release published. Signed `v2.13.0` promotes
the exact accepted `v2.13.0-rc.1` API and Web digests without rebuilding either
image.

## Overview

GoldSrcOps v2.13 extends the existing Reader Recent Activity timeline with
retained anonymous completed-round records. Readers can compare gameplay
activity with recent incident and command changes, select a dedicated Rounds
view, and follow a row to the existing bounded Events history.

This is an additive read-only API and Web release. It changes no response
shape, database schema, worker, production identity, mutation permission,
game-event producer, or delivery state.

## Included In v2.13

- Retained `round.ended` records in the bounded, globally ordered dashboard
  activity projection.
- A fourth `Rounds` count and filter on the authenticated Recent Activity page.
- Distinct gameplay-row presentation using the existing compact timeline.
- Navigation from gameplay rows to the existing authorized Events history.
- Responsive desktop and mobile layouts with all four filters visible.
- Focused API, Reader integration, browser rendering, and browser
  token-boundary coverage.

## Data And Security Boundary

The activity response reuses the existing contract and includes event identity,
source type, server identity and name, category, recorded state, and occurrence
time. Map, player and bot counts, source instance, sequence, receipt time,
intent hash, request bodies, raw database rows, server addresses, credentials,
tokens, and RCON content do not cross this browser boundary.

Bearer tokens remain server-side in the Web host. The page adds no form or
mutation control, and anonymous public routes remain unchanged.

## Compatibility

- Existing activity clients retain the same route and response shape; they may
  receive the additive `Gameplay` source value.
- The repository keeps the requested global limit after merging bounded source
  subsets, so one active source cannot silently remove every other source
  before global ordering.
- No EF Core migration, data backfill, retention change, new worker, or
  infrastructure rollout is required.
- API and Web must be published and deployed together from verified candidate
  digests. Rollback restores both v2.12 application images while leaving
  PostgreSQL, Caddy, telemetry, the game host, and dormant pilot state
  unchanged.

## Repository Acceptance

Pull request [#161](https://github.com/tov-vl/gold-src-ops/pull/161) covers
cross-source ordering and limiting, projection minimization, all four Reader
filters, Events navigation, responsive desktop/mobile rendering, and the
browser token boundary. `Change Scope`, `Quality Gate`, `Container Smoke`, and
`Browser Smoke` passed in product workflow
[#35130507621](https://github.com/tov-vl/gold-src-ops/actions/runs/35130507621)
and post-merge workflow
[#35131325453](https://github.com/tov-vl/gold-src-ops/actions/runs/35131325453).

Candidate workflow
[#35137595122](https://github.com/tov-vl/gold-src-ops/actions/runs/35137595122)
published and verified both immutable images from revision `cc80b74`. Production
retained those exact digests after an API/Web-only rollout. Four healthy samples
over 181 seconds verified public health, release identity, A2S continuity, zero
bots, zero open or newly opened incidents, zero dead letters, zero incomplete
commands, unchanged pending durable work, untouched-service continuity, and
fresh backup evidence. A separate 442-second game-host check preserved the game
service while the game-event agent remained inactive and boot-disabled.

The authenticated production page rendered All, Incidents, Commands, and Rounds
counts of 8/3/4/1. The retained gameplay row opened the existing Events history
without exposing map, population, source identity, sequence, receipt time, or
intent hash in Recent Activity. Browser storage contained no token, the page
added no mutation control, and the browser reported no warning or error.

The read-only baseline contained two reviewed deferred alert-outbox records with
alert delivery disabled. The count remained exactly two in every sample; the
release did not mutate or drain the queue. The exact ordered gate and rollback
rules are defined in
[v2.13 release readiness](v2.13-readiness.md).

## Stable Publication

Signed stable tag `v2.13.0` targets accepted candidate revision `cc80b74`.
Stable workflow
[#35143562487](https://github.com/tov-vl/gold-src-ops/actions/runs/35143562487)
skipped both image build paths, promoted the exact API and Web candidate
digests, and passed both published-image smoke jobs. Independent GHCR
inspection confirmed that the stable references resolve to those same digests.
The [GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.13.0)
is published.

## Known Limits

- Recent Activity is bounded and is not a complete gameplay-history or
  retention view.
- Gameplay rows intentionally omit map and population details; those remain on
  the existing bounded Events history and latest-round overview.
- The retained v2.11 evidence covers one bounded event, not continuous producer
  or delivery operation.
- Two reviewed pending alert-outbox records remain valid deferred work while
  delivery is disabled. v2.13 does not drain or reclassify them.
- The reference deployment remains single-node and does not claim high
  availability, multi-region resilience, long-term reliability, or an achieved
  SLO.

## References

- [v2.13 release readiness](v2.13-readiness.md)
- [Gameplay Activity Timeline pull request](https://github.com/tov-vl/gold-src-ops/pull/161)
- [Product pull request workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35130507621)
- [Product post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35131325453)
- [Candidate publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35137595122)
- [Stable publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35143562487)
- [GitHub Release v2.13.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.13.0)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.12.0 release notes](release-notes-v2.12.md)
