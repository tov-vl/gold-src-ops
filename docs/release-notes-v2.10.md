# GoldSrcOps v2.10.0 Release Notes

Status as of 2026-09-14: release-candidate preparation. Five game-event
repository slices are integrated in protected `main`; signed `v2.10.0-rc.1`
publication, migration rehearsal, and the bounded production rollout remain
pending.

## Overview

GoldSrcOps v2.10 introduces a deliberately narrow path from one anonymous
GoldSrc round-end signal to a bounded Reader view. The release contains the API
ingress contract and PostgreSQL inbox, a sandbox companion Worker with a durable
SQLite outbox, a crash-reconcilable file spool, a default-off AMX Mod X/ReAPI
sample producer, and a read-only Events page.

The production release does not enable that path. It does not provision an
OAuth machine client, install the Worker or plugin on the game host, enable
spool import or delivery, or claim that a production gameplay event was
received. Those actions belong to a separate production pilot after the
candidate is accepted.

## Included In v2.10

- `POST /api/servers/{id}/game-events` accepts contract version 1 under the
  dedicated `ingest:game-events` machine permission and a server-bound claim.
- The 4 KiB request boundary accepts only the `round.ended` event type, a stable
  event and source identity, a positive source sequence, bounded occurrence
  time, an allowlisted map, and aggregate player and bot counts.
- `goldsrcops.game_event_inbox` provides event-ID idempotency, source-sequence
  conflict detection, database constraints, ordered Reader queries, and
  bounded 45-day retention.
- `GoldSrcOps.GameEventAgent` keeps exact request bytes, persistent source
  identity, monotonic sequence allocation, leases, retry state, and terminal
  dead letters in a bounded SQLite WAL outbox.
- A strict file-spool state machine separates the game plugin from OAuth and
  HTTP delivery. Complete files move through bounded `incoming`, `processing`,
  `accepted`, and `rejected` directories with exact-byte receipts.
- A default-off sandbox AMX Mod X/ReAPI producer emits one anonymous
  `round.ended` envelope through a same-directory complete-file rename. Its
  compiler and dependencies are pinned and hash-verified by the smoke script.
- `GET /api/servers/{id}/game-events?limit=` and the static-rendered Events page
  expose only occurrence time, map, and aggregate player and bot counts under
  the existing Reader policy.

## Data And Security Boundary

The ingest contract contains no player identity, player name, chat text, IP
address, provider identifier, RCON content, arbitrary metadata, or extension
bag. Machine authorization is separate from human Reader and Operator roles,
and a token for one server cannot write events for another server.

The browser projection omits event and source IDs, source sequence, contract
version, receive time, intent hash, raw inbox state, queue state, and delivery
diagnostics. Bearer and client-credential tokens remain outside browser content
and storage. The Events page contains no mutation control.

## Compatibility And Migration

The release adds one EF Core migration,
`20260914101652_AddGameEventInboxFoundation`. It creates one new table, its
primary and foreign keys, three check constraints, and three supporting indexes.
It does not update existing rows, backfill data, alter an existing column, or
delete retained state.

The migration is additive for the retained v2.9 runtime. Normal rollback keeps
the new empty or retained table and restores the v2.9 API and Web images; the
down migration is not an application rollback mechanism. Before production,
the exact candidate migration bundle must be applied to an isolated restore of
a fresh encrypted backup, reapplied to prove idempotency, and checked with the
retained v2.9 API in read-only mode.

The API and Web remain the only container images published by the existing
release workflow. The Worker and AMX Mod X sample are source artifacts in the
signed repository release, not production installation artifacts. Their future
build, ownership, secret injection, and activation contract remains part of the
separate game-host pilot.

## Candidate Acceptance

Repository acceptance covers ingress validation, authorization and server
binding, inbox idempotency and conflict behavior, retention, SQLite outbox and
spool recovery, exact-byte delivery, pinned AMX Mod X compilation, Reader
projection minimization, static rendering, responsive layout, and the browser
token boundary. Pull requests #140 through #144 passed the required repository
checks for their exact revisions.

The candidate gate adds one fresh encrypted production backup with a `100%`
repository check and one network-isolated restore/migration rehearsal. The
subsequent rollout applies the migration as a serialized one-shot action and
recreates only API and Web from verified candidate digests. It must not
provision a machine identity, install the game-host producer or Worker, enable
delivery, or manufacture an inbox row.

Post-rollout acceptance is one 10-to-15-minute read-only smoke. It verifies
health and exact release identity, the expected migration history, empty-state
Reader behavior, authorization boundaries, browser minimization, A2S and
runtime continuity, durable queues, incidents, and backup freshness. It does
not repeat an OIDC matrix, disruptive command, recovery exercise, or
multi-hour soak.

The detailed ordered gate and rollback rules are defined in
[v2.10 release readiness](v2.10-readiness.md).

## Known Limits

- Only `round.ended` is accepted and displayed. There is no per-player event,
  score, damage, chat, or arbitrary plugin payload.
- The Reader endpoint returns at most 100 retained events and has no pagination,
  time-range selector, export, realtime stream, or cross-server view.
- The SQLite outbox and file spool are single-host components. They are not a
  distributed broker and do not claim high availability.
- Buffered plugin flush and close prove process handoff, not persistence across
  abrupt host power loss.
- Candidate acceptance without production delivery proves a dormant,
  deployable boundary, not successful game-host integration.
- The reference deployment remains single-node and does not claim long-term
  reliability, multi-region resilience, user adoption, or an achieved SLO.

## References

- [v2.10 release readiness](v2.10-readiness.md)
- [Game-event contract and local agent](game-events.md)
- [Game-event ingress foundation pull request](https://github.com/tov-vl/gold-src-ops/pull/140)
- [Sandbox companion agent pull request](https://github.com/tov-vl/gold-src-ops/pull/141)
- [Durable file-spool pull request](https://github.com/tov-vl/gold-src-ops/pull/142)
- [Sandbox AMX Mod X producer pull request](https://github.com/tov-vl/gold-src-ops/pull/143)
- [Reader projection pull request](https://github.com/tov-vl/gold-src-ops/pull/144)
- [Project backlog](backlog.md)
- [Project brief](project-brief.md)
- [v2.9.0 release notes](release-notes-v2.9.md)
- [Deployment](deployment.md)
- [PostgreSQL backup and restore](postgresql-backup.md)
- [Security](security.md)
