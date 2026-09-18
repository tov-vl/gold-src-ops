# GoldSrcOps v2.18.0 Release Notes

Status as of 2026-09-18: stable release published. Signed `v2.18.0` promotes
the exact accepted `v2.18.0-rc.2` API and Web image digests without rebuilding
either image.

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

Repository and release evidence is recorded in
[v2.18 release readiness](v2.18-readiness.md). Product and inbox-race-fix pull
requests passed their required checks. Signed candidate `v2.18.0-rc.2` at
revision `0dc6b303d7f2643c127f98c8c84736ac0f79bbaa` published and independently
verified the API digest
`sha256:4f967fc91a75bf91ff7532adaff4f80df00cc58c36dc880367d980e76c74a57b`
and Web digest
`sha256:1f1c12e7c4d36181af94202ed6ba892f9ba397d02b8a86fd5a62ae1a97582fd0`.

Production retained those exact digests after API/Web-only recreation. Four
healthy samples over 181 seconds preserved public API and Web health, exact
candidate identity, zero container restarts, A2S reachability, zero bots, no
open or new incidents, no dead letters or incomplete commands, and fresh
scheduled-backup evidence. Delivery remained disabled; two naturally existing
pending rows remained stable with zero processing work. The authenticated
Reader page rendered the same two rows with zero attempts and no missing links,
while anonymous API access remained unauthorized. Independent game-host checks
retained the active, boot-disabled, zero-restart, plugin-free service and its
single owned UDP listener; the dormant agent remained inactive and disabled.

The signed stable tag targets the accepted candidate revision. Stable workflow
[#35335400426](https://github.com/tov-vl/gold-src-ops/actions/runs/35335400426)
skipped both build paths, promoted both candidate digests, and passed both
published-image smoke jobs. The
[GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.18.0)
is published.

The acceptance path did not enable delivery, replay or delete an event, edit a
queue row, manufacture an incident, mutate Auth0, execute RCON, or restart the
game host merely to exercise this read-only surface.

The first `v2.18.0-rc.1` workflow stopped before image publication when the
PostgreSQL integration gate exposed a concurrent game-event idempotency
classification race. The fix strengthened concurrent regression coverage, and
the replacement `rc.2` candidate passed. No `rc.1` image or production rollout
occurred.

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
- [Pending Delivery Triage pull request](https://github.com/tov-vl/gold-src-ops/pull/180)
- [Inbox race fix pull request](https://github.com/tov-vl/gold-src-ops/pull/181)
- [Candidate publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35333272593)
- [Stable publication workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35335400426)
- [GitHub Release v2.18.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.18.0)
- [Release process](release-process.md)
- [Alert delivery operations](alert-delivery.md)
- [Security](security.md)
- [Project backlog](backlog.md)
- [v2.17.0 release notes](release-notes-v2.17.md)
