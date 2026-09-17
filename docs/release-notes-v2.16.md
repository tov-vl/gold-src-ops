# GoldSrcOps v2.16.0 Release Notes

Status as of 2026-09-17: stable release published. Signed `v2.16.0` promotes
the exact accepted `v2.16.0-rc.1` API and Web digests without rebuilding either
image. The GitHub Release and bounded production evidence are complete.

## Overview

GoldSrcOps v2.16 lets Readers move through older Recent Activity without
turning the operational dashboard into an unbounded history export. The API
adds a versioned opaque cursor with bounded previous and next positions. The
static-rendered Reader page adds `Newer` and `Older` navigation while retaining
the selected server, activity type, and time window.

This is an additive read-only API and Web release. It changes no database
schema, authorization policy, background worker, production identity, mutation
permission, game-event producer, delivery state, or game-host component.

## Included In v2.16

- Optional `cursor` input on `GET /api/dashboard/activity`.
- Nullable `previousCursor` and `nextCursor` response fields alongside the
  existing bounded activity items.
- A canonical version-1 cursor that fixes the UTC upper query boundary and is
  bound to the requested limit, controlled server, activity type, and time
  window.
- Validation failures for malformed, non-canonical, unsupported, out-of-range,
  or cross-scope cursors.
- A maximum activity offset of 500 and per-source materialization bounded to
  `offset + limit + 1`, with the existing public limit capped at 100.
- Deterministic reverse-chronological merge ordering across incidents,
  commands, and completed rounds.
- Static-rendered `Newer` and `Older` links that preserve the active scope;
  changing server, type, or range intentionally returns to the newest page.
- Focused cursor, service, API, repository, Reader client, static-rendering,
  responsive browser, and browser token-boundary coverage.

## Data And Security Boundary

The activity item projection is unchanged: event identity, source type, server
identity and name, category, recorded state, and occurrence time. Pagination
does not expose map, player or bot counts, source instance, sequence, receipt
time, intent hash, command input, raw database rows, server addresses,
credentials, tokens, or RCON content.

The cursor is navigation state, not an authorization credential or integrity
proof. The endpoint applies the existing Reader policy on every request and
validates cursor structure, version, range, canonical encoding, and exact query
scope. Bearer tokens remain server-side in the Web host, and the page adds no
mutation form or command control.

## Compatibility

- Existing API clients may omit `cursor` and retain the prior newest-page
  behavior.
- The response change is additive. Clients that ignore unknown JSON properties
  do not need to consume either cursor field.
- A cursor is valid only with the same limit, server, activity type, and time
  window that created it. Filter changes must begin from the newest page.
- The fixed UTC upper boundary prevents later current events from moving an
  existing page window forward. It is not a database snapshot and does not
  guarantee immutability against a late-arriving historical row.
- Cursors are versioned and intentionally disposable; clients must not persist
  them as durable bookmarks across incompatible releases.
- API and Web must be published and deployed together because the Web client
  consumes the new response fields and sends the optional cursor.
- Rollback restores both v2.15 application images while leaving PostgreSQL,
  Caddy, telemetry, the game host, and dormant pilot state unchanged.
- No EF Core migration, data backfill, retention-policy change, new worker,
  identity change, or infrastructure rollout is required.

## Repository Acceptance

Pull request [#173](https://github.com/tov-vl/gold-src-ops/pull/173) contains
the cursor contract, bounded cross-source queries, Reader navigation, and the
focused test coverage. Its pull request workflow
[#35236516265](https://github.com/tov-vl/gold-src-ops/actions/runs/35236516265)
passed `Change Scope`, `Quality Gate`, `Container Smoke`, and `Browser Smoke`.
The product was squash-merged as revision
`c3168d3c96391579bc19d30c15f4950d06be8b68`.
Its post-merge workflow
[#35237410591](https://github.com/tov-vl/gold-src-ops/actions/runs/35237410591)
passed the same four required checks.

Release acceptance follows the R1 path in the version-neutral
[release process](release-process.md). The signed candidate tag performs one
complete immutable-image workflow. Production then receives only the exact API
and Web digests and runs a three-sample, three-minute read-only smoke. It does
not repeat a game-host soak, migration or restore rehearsal, pilot activation,
Auth0 change, RCON action, alert-outbox investigation, or gameplay capture.

Release-readiness pull request
[#174](https://github.com/tov-vl/gold-src-ops/pull/174) and its post-merge
workflow
[#35238998893](https://github.com/tov-vl/gold-src-ops/actions/runs/35238998893)
passed the fail-closed documentation path. Signed candidate `v2.16.0-rc.1`
targets revision `545909f1540300aa4dd0320326e7328b8fe53d39`; workflow
[#35244049314](https://github.com/tov-vl/gold-src-ops/actions/runs/35244049314)
published and independently verified immutable API and Web images.

Production retained those exact digests after API/Web-only recreation. Four
healthy samples over 182 seconds preserved public API and Web health, exact
candidate identity, A2S reachability, zero bots, zero container and game-service
restarts, no open or newly opened incidents, no dead letters or incomplete
commands, and fresh scheduled-backup evidence. The two reviewed deferred
alert-outbox records remained unchanged while delivery stayed disabled.

An existing authenticated Reader session rendered six natural events in the
`7d` window. That data did not exceed the fixed 50-item Reader page, so the UI
correctly exposed no `Older` or `Newer` control and no production events were
manufactured. A bounded API cycle accepted a canonical cursor, rejected
malformed and mismatched-scope cursors, and confirmed that event type, range,
and server changes reset pagination. Exact-image Browser Smoke passed the token
boundary. The browser automation sandbox could not directly inspect production
browser storage, so this release makes no independent storage-content claim.

Signed stable tag `v2.16.0` targets the accepted candidate revision. Stable
workflow
[#35252019303](https://github.com/tov-vl/gold-src-ops/actions/runs/35252019303)
skipped both image builds, promoted and independently verified the exact
candidate digests, and published the
[GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.16.0).
No rollback was required. The exact evidence and claim limits are defined in
[v2.16 release readiness](v2.16-readiness.md).

## Known Limits

- Recent Activity remains bounded and is not a complete activity-history or
  retention view.
- The cursor offset is capped at 500; v2.16 intentionally does not provide an
  unbounded export or arbitrary seek.
- Counts describe the newest bounded selected-server and selected-window
  snapshot, not every retained row or the currently displayed older page.
- Cursor scope and pagination state are URL-driven and are not saved user
  preferences or durable bookmarks.
- Offset pagination shares a fixed upper time boundary but is not an immutable
  database snapshot when historical records arrive late.
- A production data set with fewer than one full page cannot expose an `Older`
  UI link. Acceptance must not manufacture operational events merely to make
  that control appear.
- Reviewed deferred alert-outbox work remains outside this release. Acceptance
  compares it with a fresh baseline and does not drain or reclassify it.
- The reference deployment remains single-node and does not claim high
  availability, multi-region resilience, long-term reliability, or an achieved
  SLO.

## References

- [v2.16 release readiness](v2.16-readiness.md)
- [Bounded Activity Pagination pull request](https://github.com/tov-vl/gold-src-ops/pull/173)
- [Product pull request workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35236516265)
- [Product post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35237410591)
- [Release readiness pull request](https://github.com/tov-vl/gold-src-ops/pull/174)
- [Readiness post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35238998893)
- [Candidate publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35244049314)
- [Stable publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35252019303)
- [GitHub Release v2.16.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.16.0)
- [Release process](release-process.md)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.15.0 release notes](release-notes-v2.15.md)
