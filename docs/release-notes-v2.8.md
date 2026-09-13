# GoldSrcOps v2.8.0 Release Notes

Status as of 2026-09-13: release-candidate preparation. The Server Trends scope
is integrated in protected `main`; signed `v2.8.0-rc.1` publication and its
bounded production smoke remain pending.

## Overview

GoldSrcOps v2.8 turns retained per-server A2S observations into a compact trend
view for day-to-day investigation. Reader users can compare recent
reachability, reachable-probe latency, and reported player and bot peaks without
transferring raw multi-day snapshot history to the browser.

This is an additive read-only release. It adds one bounded Reader endpoint and
one static-rendered Web view extension. It does not add a database migration,
lifecycle mutation, background worker, infrastructure change, OIDC change, or
new secret boundary.

## Included In v2.8

- `GET /api/servers/{id}/trends?window=1h|6h|24h|7d`, protected by the existing
  `Reader` policy.
- Fixed-size windows containing 12 five-minute, 24 fifteen-minute, 24 hourly,
  or 28 six-hour buckets.
- PostgreSQL-side aggregation of sample count, reachable count,
  reachable-sample latency, and reported player and bot peaks.
- Sample-weighted summary values rather than averages of bucket percentages or
  bucket averages.
- Explicit `unknown` buckets when no retained observation exists; missing data
  is excluded from observed percentages and averages.
- Static-rendered range controls and separate accessible trend tracks on
  `/operator/servers/{id}/history`.
- Existing recent-snapshot and incident-history views preserved alongside the
  new aggregate.
- Responsive desktop and mobile layouts with Reader, Operator, and browser
  token-boundary coverage.

## Data And Security Boundary

The API accepts only the four fixed windows and returns 12 to 28 aggregate
buckets. PostgreSQL filters by one server and one bounded time range before
materialization; raw multi-day snapshots are not returned to the Web host or
browser.

Bearer tokens remain server-side in the Web host. The trend contract omits
server addresses, credentials, secret references, command data, incident
reasons, probe failure reasons, and raw database records. The trend panel adds
no form or mutation control, and the anonymous public dashboard remains
unchanged.

## Compatibility

- Existing API and Web routes remain available; the trend endpoint and response
  contract are additive.
- No EF Core migration, schema maintenance, data backfill, or new retention job
  is required.
- API and Web are deployed together from verified candidate digests under the
  existing production contract.
- Rollback restores both v2.7 application images and leaves PostgreSQL, Caddy,
  telemetry services, retained snapshots, and the game host unchanged.

## Candidate Acceptance

Repository acceptance covers fixed-window validation, bucket alignment,
weighted summaries, missing-data semantics, PostgreSQL-side aggregation,
response minimization, Reader authorization, API-client URI construction,
static rendering, responsive browser checks, and the browser token boundary.

Production acceptance is deliberately short and read-only: one 10-15 minute
smoke verifies public health, release identity, all four authenticated trend
windows, the static-rendered history view, browser secret boundaries, A2S
continuity, zero bots, durable-work and incident counts, unchanged restart
counts, and backup freshness. It must not manufacture observations or
incidents, submit a mutation, or change monitoring cadence. A fresh Reader-only
production login is not required when unchanged OIDC and authorization are
covered by Browser Smoke on the exact immutable Web image; any such
substitution must be recorded without claiming a fresh Reader session.

The smoke does not repeat backup/restore, lifecycle commands, RCON traffic,
OIDC reconfiguration, or a multi-day soak. The exact ordered gate and rollback
rules are defined in [v2.8 release readiness](v2.8-readiness.md).

## Known Limits

- The trends summarize recorded A2S probe outcomes. They are not service uptime,
  a complete latency distribution, or evidence that an SLO has been achieved.
- Missing buckets remain unknown. Snapshot retention, collection gaps, or a
  newly registered server can therefore leave part of a selected window empty.
- Latency averages include only reachable observations with a recorded latency.
- Player and bot values are server-reported bucket peaks, not identities,
  sessions, unique visitors, or adoption evidence.
- The four fixed windows cannot be replaced with arbitrary ranges, pagination,
  raw export, comparison overlays, or long-term analytics in this release.
- The reference deployment remains single-node and does not claim high
  availability, multi-region resilience, or long-term reliability.

## References

- [v2.8 release readiness](v2.8-readiness.md)
- [Server Trends pull request](https://github.com/tov-vl/gold-src-ops/pull/132)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.7.0 release notes](release-notes-v2.7.md)
- [Security](security.md)
