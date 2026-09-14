# GoldSrcOps v2.9.0 Release Notes

Status as of 2026-09-14: release-candidate preparation. The Recent Operations
Activity scope is integrated in protected `main`; signed `v2.9.0-rc.1`
publication and its bounded production smoke remain pending.

## Overview

GoldSrcOps v2.9 gives Reader users one compact timeline for recent incident
lifecycle changes and command-state transitions across the fleet. Operators can
move from a recent event to the existing incident investigation or per-server
command history without inspecting separate fleet views first.

This is an additive read-only release. It adds one bounded Reader endpoint and
one static-rendered Web page. It does not add a database migration, lifecycle
mutation, background worker, infrastructure change, OIDC change, or new secret
boundary.

## Included In v2.9

- `GET /api/dashboard/activity?limit=`, protected by the existing `Reader`
  policy, with a default limit of 50 and an accepted range of 1 to 100.
- Deterministic reverse-chronological merging of recent incident and command
  records, including stable source-type and source-identifier tie breaking.
- At most `limit` incidents and `limit` commands materialized before the final
  bounded merge returns at most `limit` events.
- A minimized response containing event identity, server identity and name,
  category, state, and occurrence time only.
- Static-rendered All, Incidents, and Commands views on `/operator/activity`.
- Direct links to existing incident investigation and per-server command
  history routes.
- Responsive desktop and mobile navigation with Reader, Operator, and browser
  token-boundary coverage.

## Data And Security Boundary

The API rejects limits outside 1 to 100. Each source query is ordered and
bounded before materialization, and the final in-memory merge applies a second
deterministic bound. The endpoint does not provide arbitrary time ranges,
pagination, export, or unbounded fleet history.

Bearer tokens remain server-side in the Web host. The activity contract omits
server addresses, credentials, secret references, incident reasons, command
payloads, requesters, results, failure details, RCON content, and raw database
records. The page adds no form or mutation control, and the anonymous public
dashboard remains unchanged.

## Compatibility

- Existing API and Web routes remain available; the activity endpoint and page
  are additive.
- No EF Core migration, schema maintenance, data backfill, retention change, or
  new worker is required.
- API and Web are deployed together from verified candidate digests under the
  existing production contract.
- Rollback restores both v2.8 application images and leaves PostgreSQL, Caddy,
  telemetry services, retained records, and the game host unchanged.

## Candidate Acceptance

Repository acceptance covers limit validation, deterministic ordering and tie
breaking, bounded source materialization, response minimization, Reader
authorization, API-client URI construction, static HTML filtering, responsive
navigation, and the browser token boundary. The complete `Quality Gate`,
`Container Smoke`, and `Browser Smoke` passed for the product pull request.

Production acceptance follows the additive read-only row in the risk-based
release policy. One read-only session must collect at least three healthy
post-rollout samples spanning at least three minutes. It verifies public health,
release identity, the bounded activity API, all three static-rendered activity
views, browser secret boundaries, A2S continuity, zero bots, durable-work and
incident counts, unchanged restart counts, and backup freshness. It must not
manufacture an incident or command, submit a mutation, or change monitoring
cadence.

A fresh Reader-only production login is not required when OIDC and
authorization are unchanged and Reader concealment is covered by Browser Smoke
on the exact immutable Web image. An existing valid Operator session or
non-interactive contract evidence may be used, and the substitution must be
recorded without claiming a fresh Reader session.

The smoke does not repeat backup/restore, lifecycle commands, RCON traffic,
OIDC reconfiguration, or a multi-hour or multi-day soak. The exact ordered gate
and rollback rules are defined in [v2.9 release readiness](v2.9-readiness.md).

## Known Limits

- The timeline contains only the latest bounded incident and command records;
  it is not a complete audit export or retention guarantee.
- Occurrence time represents the latest relevant lifecycle time for each source
  record. Intermediate transitions are not expanded into separate events.
- The page has fixed All, Incidents, and Commands views. It does not provide
  free-text search, arbitrary date ranges, pagination, saved filters, realtime
  updates, or cross-event correlation.
- Sanitized state and category labels are operational summaries, not the full
  incident or command record. Existing detail routes remain the source for
  authorized investigation.
- The reference deployment remains single-node and does not claim high
  availability, multi-region resilience, long-term reliability, or an achieved
  SLO.

## References

- [v2.9 release readiness](v2.9-readiness.md)
- [Recent Operations Activity pull request](https://github.com/tov-vl/gold-src-ops/pull/137)
- [Risk-based release gates pull request](https://github.com/tov-vl/gold-src-ops/pull/136)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.8.0 release notes](release-notes-v2.8.md)
- [Deployment](deployment.md)
- [Security](security.md)
