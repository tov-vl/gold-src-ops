# GoldSrcOps v2.18.0 Release Notes

Status as of 2026-09-18: product validation in progress. Candidate publication,
production acceptance, stable promotion, and the GitHub Release are pending.

## Overview

GoldSrcOps v2.18 adds Pending Delivery Triage for the durable alert outbox. An
authenticated Reader can inspect bounded pending work in claim order, see retry
timing and linked incident state, and move through opaque cursor pages without
direct database access.

This is an additive read-only API and Web release. It changes no database
schema, delivery worker, retry behavior, authorization role, identity,
configuration, event producer, webhook, or game-host component.

## Included In v2.18

- Reader-authorized `GET /api/alert-delivery/pending?limit=&cursor=`.
- Stable ascending ordering by next-attempt time, occurrence time, and event ID.
- Default page size 50 and maximum page size 200.
- A versioned opaque cursor that rejects malformed and unsupported input.
- Safe event identity/type, occurrence time, attempt count, next-attempt time,
  linked incident state, and server display name/ID.
- Explicit `Missing` state for an outbox row whose incident relation is absent.
- A static-rendered `/operator/alert-delivery/pending` page with empty, error,
  linked-incident, and bounded pagination states.
- A direct path from the Alert Delivery Overview into pending triage.

## Data And Security Boundary

The response and page exclude event payloads and payload versions, delivery
errors, claim IDs and times, webhook addresses, authorization values, server
addresses and ports, credentials, tokens, RCON content, and raw database rows.
They expose no form or delivery, replay, deletion, reclassification, or queue
mutation control.

The endpoint and page reuse the existing Reader policy. Anonymous access remains
unauthorized. Existing Operator-only replay behavior and authorization are
unchanged, and browser bearer tokens remain server-side in the Web host.

## Compatibility And Semantics

- Existing clients and response contracts are unchanged; the route and DTOs are
  additive.
- API and Web should be deployed together because the page consumes the new
  route.
- The cursor is a navigation token, not a durable snapshot or queue lease.
- Pending rows can move or leave the result set while a Reader pages through a
  live queue; the view does not claim transactional consistency across pages.
- `Missing` reports relation drift for investigation and does not authorize a
  data correction.
- The query reuses the existing `Pending` partial index; no migration, backfill,
  queue drain, backup, restore rehearsal, or previous-runtime schema rehearsal
  is required.
- Rollback restores the previous API and Web images while leaving PostgreSQL,
  telemetry, the game host, delivery configuration, and outbox rows unchanged.

## Acceptance

Repository and release evidence will be recorded in
[v2.18 release readiness](v2.18-readiness.md). The release follows the R1 path:
one complete product CI pass, immutable candidate publication, an exact-digest
API/Web-only rollout, at least three healthy read-only samples over at least
three minutes, and stable promotion without rebuilding.

The acceptance path must not enable delivery, replay or delete an event, edit a
queue row, manufacture an incident, mutate Auth0, execute RCON, or restart the
game host merely to exercise this read-only surface.

## Known Limits

- This is current queue triage, not queue history, throughput analysis, or a
  delivery SLO.
- The page does not auto-refresh or stream changes.
- Forward cursor navigation does not provide a previous-page token.
- The view cannot explain a delivery receiver that has never been configured or
  prove dispatcher liveness.
- A 3-minute production smoke is short release evidence, not proof of long-term
  reliability or an achieved SLO.

## References

- [v2.18 release readiness](v2.18-readiness.md)
- [Release process](release-process.md)
- [Alert delivery operations](alert-delivery.md)
- [Security](security.md)
- [Project backlog](backlog.md)
- [v2.17.0 release notes](release-notes-v2.17.md)
