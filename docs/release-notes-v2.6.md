# GoldSrcOps v2.6.0 Release Notes

Status as of 2026-09-12: `v2.6.0-rc.1` is accepted in production. Repository
checks, immutable API/Web publication, digest-pinned rollout, and the bounded
read-only smoke passed; stable digest-preserving publication remains pending.

## Overview

GoldSrcOps v2.6 turns the authenticated server inventory into an operational
triage view. It combines aggregate fleet health and the current state of every
controlled server in one Reader projection, then makes attention states,
search, and deterministic sorting available through the static-rendered Web
portal.

This is an additive read-only release. It does not add a migration, lifecycle
mutation, infrastructure change, or new secret boundary.

## Included In v2.6

- `GET /api/dashboard/fleet`, protected by the existing `Reader` policy.
- Aggregate total, enabled, paused, online, offline, unknown, and open-incident
  counts calculated from the same projection as the fleet rows.
- Per-server current state, latest observation time, latency, map, population,
  latest bot count, consecutive failures, and open-incident count.
- Freshness derived from the configured polling interval, with missing or
  overdue observations classified as stale.
- Attention classification for offline, unknown, stale, and incident-bearing
  servers. Open incidents remain actionable after monitoring is paused.
- Static-rendered state tabs, search by name, endpoint, or map, and sorting by
  attention, name, or latest observation.
- Responsive desktop and mobile layouts with an accessible active-view marker.

## Data And Security Boundary

The fleet response intentionally omits server notes, credential metadata and
values, secret references, failure reasons, incident details, command payloads,
and raw database records. Existing mutation endpoints remain Operator-only and
are not called by Fleet Triage.

The Web host keeps bearer tokens server-side. Reader users receive no lifecycle
forms or mutation controls, while Operator users retain the existing guarded
workflows on separate pages.

## Compatibility

- Existing API and Web routes remain available.
- No EF Core migration or database maintenance is required.
- API and Web must be deployed together from the verified candidate digests.
- Rollback restores both v2.5 application images and leaves PostgreSQL, Caddy,
  telemetry services, and the game host unchanged.

## Candidate Acceptance

Repository acceptance covers formatting, build, unit and API integration
tests, PostgreSQL query translation, Reader authorization, exact JSON field
allowlisting, API-client behavior, static rendering, responsive browser checks,
and a real browser filter workflow.

Production acceptance is deliberately short and read-only: one 10-15 minute
smoke verifies public health, release identity, Reader and Operator rendering,
Fleet Triage filters, browser secret boundaries, A2S continuity, zero bots,
empty incident and durable-work queues, unchanged restart counts, and backup
freshness. It does not repeat backup/restore, lifecycle commands, OIDC
reconfiguration, or a multi-day soak.

The exact ordered gate and rollback rules are defined in
[v2.6 release readiness](v2.6-readiness.md).

The accepted candidate is revision
`b7c95befe135d6ef644c4158b33cd8987aad5664`. Its API image digest is
`sha256:5d26f931b06886b0d40be01f3bb719b3cde6b639516c443d8944ff550141bdd2`
and its Web image digest is
`sha256:e04069a86aa53c49c93151eae1bbb18d1287e31c65da3dfe107a7856917f3982`.
The tag workflow and every publication verification job passed in
[workflow #34703040195](https://github.com/tov-vl/gold-src-ops/actions/runs/34703040195).

Production retained the candidate after a 10 minute 38 second read-only smoke.
Public health, release identity, container and game-service continuity, A2S
reachability, zero bots, empty incident and durable-work queues, and scheduled
backup freshness passed. Authenticated Operator rendering, state filters,
search, sorting, browser secret boundaries, and console cleanliness also
passed without submitting a mutation. Reader-only concealment was reused from
the exact-image Browser Smoke because production OIDC and authorization did not
change; a fresh Reader-only production login was deliberately not claimed.

## Known Limits

- The fleet projection returns the complete configured fleet and does not yet
  paginate. This is appropriate for the current small MVP deployment, not an
  unbounded multi-tenant claim.
- Search and sorting are applied by the static-rendered Web host after one API
  request. They are not separate server-side query contracts.
- Freshness is operational triage derived from polling configuration, not an
  availability SLO measurement.
- Fleet Triage does not replace incident detail, history, command audit, or
  lifecycle review pages.
- The reference deployment remains single-node and does not claim high
  availability or long-term reliability.

## References

- [v2.6 release readiness](v2.6-readiness.md)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.5.0 release notes](release-notes-v2.5.md)
- [Security](security.md)
