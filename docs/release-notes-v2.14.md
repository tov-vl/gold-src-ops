# GoldSrcOps v2.14.0 Release Notes

Status as of 2026-09-17: candidate preparation. The product slice is integrated
in protected `main`; no v2.14 candidate tag, production rollout, stable tag, or
GitHub Release has been published yet.

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

Release acceptance follows the R1 path in the version-neutral
[release process](release-process.md). The signed candidate tag performs one
complete immutable-image workflow. Production then receives only the exact API
and Web digests and runs a three-sample, three-minute read-only smoke. It does
not repeat a game-host soak, migration or restore rehearsal, pilot activation,
Auth0 change, RCON action, alert-outbox investigation, or gameplay capture.

Candidate identity, rollout evidence, and stable publication remain pending.
The exact ordered gate and rollback rules are defined in
[v2.14 release readiness](v2.14-readiness.md).

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
- [Release process](release-process.md)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.13.0 release notes](release-notes-v2.13.md)
