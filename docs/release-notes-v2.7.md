# GoldSrcOps v2.7.0 Release Notes

Status as of 2026-09-13: release-candidate preparation. The Incident
Investigation scope is integrated in protected `main`; signed `v2.7.0-rc.1`
publication and its bounded production smoke remain pending.

## Overview

GoldSrcOps v2.7 gives Reader users one focused path from fleet attention to a
durable availability incident and the A2S observations nearest its opening and
recovery boundaries. The view keeps the historical record distinct from the
server's current monitoring state and remains useful when retained snapshots
are incomplete.

This is an additive read-only release. It does not add an API endpoint,
database migration, lifecycle mutation, background worker, infrastructure
change, or new secret boundary.

## Included In v2.7

- `/operator/incidents/{id}`, protected by the existing `Reader` policy.
- Durable incident timing, state, type, failure streak, start reason, and
  resolution alongside the associated server name.
- Current server and monitoring context clearly separated from the historical
  incident record.
- At most 50 A2S snapshots from each 15-minute window around incident opening
  and recovery, de-duplicated and ordered by observation time.
- Explicit opening and recovery labels for boundary observations, with
  reachability, map, population, latency, and bounded probe outcome.
- Graceful not-found, missing-snapshot, and unavailable-upstream states that do
  not overstate the available evidence.
- Direct navigation from Fleet Triage, the incident list, and per-server
  history to the exact incident record.
- Responsive desktop and mobile layouts with Reader, Operator, and browser
  token-boundary coverage.

## Data And Security Boundary

The Web host composes existing Reader-authorized incident, server, current
status, and bounded snapshot endpoints. It keeps bearer tokens server-side and
does not request credentials, secret references, command payloads, alert
delivery data, or raw database records for the investigation page.

The route exposes no POST form or mutation control. Reader users cannot submit
lifecycle or RCON actions from the view; Operator workflows remain on their
existing separately guarded pages. Incident and probe reasons are authenticated
operational data and are not added to the sanitized public dashboard.

## Compatibility

- Existing API and Web routes remain available.
- No API contract, EF Core migration, or database maintenance is required.
- API and Web are deployed together from the verified candidate digests under
  the existing production contract.
- Rollback restores both v2.6 application images and leaves PostgreSQL, Caddy,
  telemetry services, and the game host unchanged.

## Candidate Acceptance

Repository acceptance covers formatting, build, unit and Web integration
tests, API-client URI construction, Reader and Operator authorization, missing
records, bounded and overlapping snapshot windows, static rendering,
responsive browser checks, and the browser token boundary.

Production acceptance is deliberately short and read-only: one 10-15 minute
smoke verifies public health, release identity, an already-retained incident
through the authenticated investigation path, browser secret boundaries, A2S
continuity, zero bots, durable-work and incident counts, unchanged restart
counts, and backup freshness. It must not manufacture an incident or submit a
mutation. A fresh Reader-only production login is not required when unchanged
OIDC and authorization are covered by Browser Smoke on the exact immutable Web
image; any such substitution must be recorded without claiming a fresh Reader
session.

The smoke does not repeat backup/restore, lifecycle commands, RCON traffic,
OIDC reconfiguration, or a multi-day soak. The exact ordered gate and rollback
rules are defined in [v2.7 release readiness](v2.7-readiness.md).

## Known Limits

- Boundary observations provide nearby operational evidence, not a complete
  causal trace or proof of the incident's root cause.
- Snapshot retention can leave an incident record without observations around
  one or both boundaries. The page reports that absence instead of inferring
  missing history.
- Each opening or recovery query is capped at 50 snapshots. The view does not
  paginate or load an unbounded incident timeline.
- Current server status may differ from the historical incident state and is
  intentionally presented as current context.
- The release does not add incident acknowledgement, annotation, ownership,
  export, or postmortem workflows.
- The reference deployment remains single-node and does not claim high
  availability, long-term reliability, or an achieved SLO.

## References

- [v2.7 release readiness](v2.7-readiness.md)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.6.0 release notes](release-notes-v2.6.md)
- [Security](security.md)
