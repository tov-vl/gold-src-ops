# GoldSrcOps v2.9.0 Release Notes

Status as of 2026-09-14: stable release published. Signed `v2.9.0-rc.1`
publication, digest-pinned production acceptance, signed `v2.9.0` promotion,
published-image verification, and the GitHub Release are complete.

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

## Release Acceptance

Repository acceptance covers limit validation, deterministic ordering and tie
breaking, bounded source materialization, response minimization, Reader
authorization, API-client URI construction, static HTML filtering, responsive
navigation, and the browser token boundary. The complete `Quality Gate`,
`Container Smoke`, and `Browser Smoke` passed for the product pull request and
signed candidate workflow
[#34822994088](https://github.com/tov-vl/gold-src-ops/actions/runs/34822994088).

The exact candidate API and Web digests were deployed together without a schema
operation. Three healthy read-only samples spanning 405.61 seconds confirmed
public health, release identity, recent reachable A2S state with zero bots,
fresh backup evidence, unchanged control-plane and game-service continuity, and
zero open incidents or pending durable work. The existing Operator session
rendered six minimized events across All, Incidents, and Commands, with correct
ordering, authorized detail links, no mutation control, no bearer-like browser
value, and no console warning or error.

OIDC and authorization were unchanged, so Reader-only concealment was reused
from Browser Smoke on the exact immutable Web image; no fresh Reader login is
claimed. The acceptance did not manufacture an incident or command and did not
repeat backup/restore, lifecycle commands, RCON traffic, OIDC configuration, or
a multi-hour or multi-day soak.

Signed stable tag `v2.9.0` targets revision
`1ad782e50722ac9fba681125ace4deda222ab16c`. Stable workflow
[#34825735706](https://github.com/tov-vl/gold-src-ops/actions/runs/34825735706)
skipped both image builds, promoted the exact accepted candidate digests, and
independently smoke-tested both stable references. The bounded evidence supports
this additive release, not a long-term reliability, high-availability, or
achieved-SLO claim. Exact identity, evidence, and rollback details are in
[v2.9 release readiness](v2.9-readiness.md).

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

- [GoldSrcOps v2.9.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.9.0)
- [v2.9 release readiness](v2.9-readiness.md)
- [Recent Operations Activity pull request](https://github.com/tov-vl/gold-src-ops/pull/137)
- [Risk-based release gates pull request](https://github.com/tov-vl/gold-src-ops/pull/136)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.8.0 release notes](release-notes-v2.8.md)
- [Deployment](deployment.md)
- [Security](security.md)
