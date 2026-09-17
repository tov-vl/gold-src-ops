# GoldSrcOps v2.14.0 Release Notes

Status as of 2026-09-17: stable release published. Signed `v2.14.0` promotes
the exact accepted `v2.14.0-rc.1` API and Web digests without rebuilding either
image.

## Overview

GoldSrcOps v2.14 lets Readers scope the bounded Recent Activity investigation
to one controlled server and one event type. Server scope persists across the
All, Incidents, Commands, and Rounds tabs as well as Refresh and empty-result
navigation, while `Clear server` restores the all-server view.

This is an additive read-only API and Web release. It changes no response body,
database schema, authorization policy, worker, production identity, mutation
permission, game-event producer, or delivery state.

## Included In v2.14

- Optional `serverId` and validated
  `kind=all|incidents|commands|gameplay` activity query parameters.
- Per-source filtering before each bounded query limit, followed by the
  existing global newest-first limit.
- A controlled-server selector on the static-rendered Recent Activity page.
- Server scope preserved across all four type tabs, Refresh, and empty-result
  navigation, with an explicit control that removes only the server scope.
- Counts derived from one all-source bounded snapshot for the selected server,
  with a separate typed query for filtered rows.
- Focused API, repository, Reader client, static-rendering, responsive browser,
  and browser token-boundary coverage.

## Data And Security Boundary

The activity response keeps the existing contract: identity, source type,
server identity and name, category, recorded state, and occurrence time. The
new request filters do not expose map, player or bot counts, source instance,
sequence, receipt time, intent hash, request bodies, raw database rows, server
addresses, credentials, tokens, or RCON content.

Bearer tokens remain server-side in the Web host. The page adds no mutation
form or command control, and anonymous public routes remain unchanged.

## Compatibility

- Existing clients may omit both new parameters and retain the previous
  all-server, all-source behavior.
- The response shape is unchanged; the new behavior is request-only and
  additive.
- Invalid `kind` values now receive a validation problem instead of an
  unintentionally broadened result.
- API and Web must be published and deployed together from verified candidate
  digests. Rollback restores both v2.13 application images while leaving
  PostgreSQL, Caddy, telemetry, the game host, and dormant pilot state
  unchanged.
- No EF Core migration, data backfill, retention change, new worker, identity
  change, or infrastructure rollout is required.

## Repository Acceptance

Pull request [#166](https://github.com/tov-vl/gold-src-ops/pull/166) covers
filter validation and forwarding, per-source pre-limit filtering, query-string
encoding, selected-server count behavior, scope-preserving static rendering,
responsive desktop/mobile Chromium, and the browser token boundary. `Change
Scope`, `Quality Gate`, `Container Smoke`, and `Browser Smoke` passed in product
workflow
[#35204950777](https://github.com/tov-vl/gold-src-ops/actions/runs/35204950777)
and post-merge workflow
[#35205688559](https://github.com/tov-vl/gold-src-ops/actions/runs/35205688559).

Candidate workflow
[#35209067745](https://github.com/tov-vl/gold-src-ops/actions/runs/35209067745)
published and verified both immutable images from revision `c5402de`. Production
retained those exact digests after an API/Web-only rollout. Three healthy
samples over 181 seconds verified public health, release identity, A2S
continuity, zero bots, zero open or newly opened incidents, zero dead letters,
zero incomplete commands, unchanged pending durable work, untouched-service
continuity, and fresh backup evidence. A separate 234-second game-host check
preserved the game service while the game-event agent remained inactive and
boot-disabled.

The authenticated production page rendered scoped All, Incidents, Commands,
and Rounds counts of 8/3/4/1. Each type filter showed only its own source,
Refresh preserved both scopes, and `Clear server` removed only the server scope.
Browser storage contained no token-like entry, the page exposed no mutation
control, and the browser reported no warning or error.

The read-only baseline contained two reviewed deferred alert-outbox records with
alert delivery disabled. The count remained exactly two in every sample; the
release did not mutate or drain the queue. The exact ordered gate and rollback
rules are defined in [v2.14 release readiness](v2.14-readiness.md).

## Stable Publication

Signed stable tag `v2.14.0` targets accepted candidate revision `c5402de`.
Stable workflow
[#35212041218](https://github.com/tov-vl/gold-src-ops/actions/runs/35212041218)
skipped both image build paths, promoted the exact API and Web candidate
digests, and passed both published-image smoke jobs. Independent GHCR
inspection confirmed that the stable references resolve to those same digests.
The [GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.14.0)
is published.

## Known Limits

- Recent Activity remains bounded and is not a complete activity-history or
  retention view.
- Counts describe the bounded selected-server snapshot, not all retained rows.
- Server scope is URL-driven and is not a saved user preference.
- Reviewed deferred alert-outbox work remains outside this release. Acceptance
  compares it with a fresh baseline and does not drain or reclassify it.
- The reference deployment remains single-node and does not claim high
  availability, multi-region resilience, long-term reliability, or an achieved
  SLO.

## References

- [v2.14 release readiness](v2.14-readiness.md)
- [Scoped Activity Investigation pull request](https://github.com/tov-vl/gold-src-ops/pull/166)
- [Product pull request workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35204950777)
- [Product post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35205688559)
- [Release readiness pull request](https://github.com/tov-vl/gold-src-ops/pull/167)
- [Readiness post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35208624054)
- [Candidate publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35209067745)
- [Stable publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35212041218)
- [GitHub Release v2.14.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.14.0)
- [Release process](release-process.md)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.13.0 release notes](release-notes-v2.13.md)
