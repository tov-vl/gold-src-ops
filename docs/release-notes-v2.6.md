# GoldSrcOps v2.6.0 Release Notes

Status as of 2026-09-12: release-candidate preparation. The Fleet Triage scope
is integrated in protected `main`; signed `v2.6.0-rc.1` publication and its
bounded production smoke remain pending.

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
