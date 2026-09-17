# GoldSrcOps v2.15.0 Release Notes

Status as of 2026-09-17: stable release published. Signed `v2.15.0` promotes
the exact accepted `v2.15.0-rc.1` API and Web digests without rebuilding either
image. The GitHub Release and bounded production evidence are complete.

## Overview

GoldSrcOps v2.15 gives Readers an explicit investigation horizon for Recent
Activity and predictable contextual entry points into that view. Readers can
select `1h`, `6h`, `24h`, or `7d`; the page defaults to `24h` and preserves the
selected range alongside server and event-type scope. Server views open the
same `24h` activity view already scoped to that server, while incident
investigation opens the matching server-scoped incident activity.

This is an additive read-only API and Web release. It changes no response body,
database schema, authorization policy, worker, production identity, mutation
permission, game-event producer, delivery state, or game-host component.

## Included In v2.15

- Optional, validated `window=1h|6h|24h|7d` on
  `GET /api/dashboard/activity`.
- Effective incident, command, and completed-round time filtering before each
  bounded source query applies `Take(limit)`.
- A `24h` default on the static-rendered Recent Activity page, with range
  preserved across server filtering, event-type tabs, refresh, explicit server
  clearing, and empty-result navigation.
- Server-section Activity links that retain the server and open the `24h`
  window.
- An incident-investigation link that opens `kind=incidents` for the affected
  server in the `24h` window.
- Focused service, API, PostgreSQL, Reader client, static-rendering, responsive
  browser, and browser token-boundary coverage.

## Data And Security Boundary

The activity response keeps the existing contract: event identity, source
type, server identity and name, category, recorded state, and occurrence time.
The new time filter and contextual links do not expose map, player or bot
counts, source instance, sequence, receipt time, intent hash, request bodies,
raw database rows, server addresses, credentials, tokens, or RCON content.

Bearer tokens remain server-side in the Web host. The page and contextual links
add no mutation form or command control, and anonymous public routes remain
unchanged.

## Compatibility

- Existing API clients may omit `window` and retain the prior bounded
  latest-events behavior.
- The response shape is unchanged; the new behavior is request-only and
  additive.
- Invalid window values receive a validation problem instead of an
  unintentionally widened result.
- Contextual links are ordinary URL-driven navigation and create no new
  endpoint or client-side state.
- API and Web must be published and deployed together because the Web client
  sends the new optional query parameter.
- Rollback restores both v2.14 application images while leaving PostgreSQL,
  Caddy, telemetry, the game host, and dormant pilot state unchanged.
- No EF Core migration, data backfill, retention-policy change, new worker,
  identity change, or infrastructure rollout is required.

## Repository Acceptance

Pull request [#169](https://github.com/tov-vl/gold-src-ops/pull/169) covers
window parsing, effective-time filtering, omission compatibility, invalid-input
handling, Reader range preservation, responsive rendering, and PostgreSQL query
translation. Its pull request workflow
[#35215971370](https://github.com/tov-vl/gold-src-ops/actions/runs/35215971370)
and post-merge workflow
[#35216674959](https://github.com/tov-vl/gold-src-ops/actions/runs/35216674959)
passed `Change Scope`, `Quality Gate`, `Container Smoke`, and `Browser Smoke`.

Pull request [#170](https://github.com/tov-vl/gold-src-ops/pull/170) covers
server- and incident-scoped entry points, rendered-link contracts, and real
browser navigation on desktop and mobile. Its pull request workflow
[#35219216273](https://github.com/tov-vl/gold-src-ops/actions/runs/35219216273)
and post-merge workflow
[#35219892789](https://github.com/tov-vl/gold-src-ops/actions/runs/35219892789)
passed the same four required checks.

Release acceptance follows the R1 path in the version-neutral
[release process](release-process.md). The signed candidate tag performs one
complete immutable-image workflow. Production then receives only the exact API
and Web digests and runs a three-sample, three-minute read-only smoke. It does
not repeat a game-host soak, migration or restore rehearsal, pilot activation,
Auth0 change, RCON action, alert-outbox investigation, or gameplay capture.

Release-readiness pull request
[#171](https://github.com/tov-vl/gold-src-ops/pull/171) and its post-merge
workflow
[#35222234795](https://github.com/tov-vl/gold-src-ops/actions/runs/35222234795)
passed the documentation fast path. Signed candidate `v2.15.0-rc.1` targets
revision `fd63e150afdda187f20d78d7cbbcbb7e8dd7931f`; workflow
[#35223239116](https://github.com/tov-vl/gold-src-ops/actions/runs/35223239116)
published and verified immutable API and Web images.

Production retained those exact digests after API/Web-only recreation. Four
healthy samples over 182 seconds preserved public health, A2S reachability,
zero bots, zero restarts, no open or newly opened incidents, no dead letters or
incomplete commands, and fresh backup evidence. Authenticated UI acceptance
verified all four fixed windows, contextual server and incident entry points,
scope-preserving Refresh and type tabs, and server-only clearing. The two
reviewed deferred alert-outbox records remained unchanged with delivery
disabled.

Signed stable tag `v2.15.0` targets the accepted candidate revision. Stable
workflow
[#35227905125](https://github.com/tov-vl/gold-src-ops/actions/runs/35227905125)
skipped both image builds, promoted and independently verified the exact
candidate digests, and published the
[GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.15.0).
No rollback was required. The exact evidence and claim limits are defined in
[v2.15 release readiness](v2.15-readiness.md).

## Known Limits

- Recent Activity remains bounded and is not a complete activity-history or
  retention view.
- Counts describe the bounded selected-server and selected-window snapshot,
  not all retained rows.
- Window, server, and source scope are URL-driven and are not saved user
  preferences.
- The four fixed windows do not provide arbitrary date-range search or
  pagination.
- Reviewed deferred alert-outbox work remains outside this release. Acceptance
  compares it with a fresh baseline and does not drain or reclassify it.
- The reference deployment remains single-node and does not claim high
  availability, multi-region resilience, long-term reliability, or an achieved
  SLO.

## References

- [v2.15 release readiness](v2.15-readiness.md)
- [Bounded Activity Windows pull request](https://github.com/tov-vl/gold-src-ops/pull/169)
- [Scoped Activity Entry Points pull request](https://github.com/tov-vl/gold-src-ops/pull/170)
- [First product post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35216674959)
- [Second product post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35219892789)
- [Release readiness pull request](https://github.com/tov-vl/gold-src-ops/pull/171)
- [Candidate publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35223239116)
- [Stable publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35227905125)
- [GitHub Release v2.15.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.15.0)
- [Release process](release-process.md)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.14.0 release notes](release-notes-v2.14.md)
