# GoldSrcOps v2.10.0 Release Notes

Status as of 2026-09-15: stable release published. Signed `v2.10.0-rc.1`
publication, fresh backup and isolated migration rehearsal, digest-pinned
dormant production acceptance, signed `v2.10.0` promotion, published-image
verification, and the GitHub Release are complete.

## Overview

GoldSrcOps v2.10 introduces a deliberately narrow path from one anonymous
GoldSrc round-end signal to a bounded Reader view. The release contains the API
ingress contract and PostgreSQL inbox, a sandbox companion Worker with a durable
SQLite outbox, a crash-reconcilable file spool, a default-off AMX Mod X/ReAPI
sample producer, and a read-only Events page.

The accepted production candidate does not enable that path. It does not
provision an OAuth machine client, install the Worker or plugin on the game
host, enable spool import or delivery, or claim that a production gameplay
event was received. Those actions belong to a separate production pilot and
are not a hidden stable-release requirement.

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
down migration is not an application rollback mechanism. The exact candidate
migration bundle was applied to an isolated restore of a fresh encrypted
backup, reapplied without changing the migration inventory, and checked with
the retained v2.9 API in read-only mode before production rollout.

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
checks for their exact revisions. Pull request #145 froze the release boundary,
and signed candidate workflow
[#34956379584](https://github.com/tov-vl/gold-src-ops/actions/runs/34956379584)
built and independently verified both immutable images from revision
`c5bb47d56a6c11dbecb037ba90cdb154fe9fc3f6`.

The accepted API digest is
`sha256:bea38877af7294869369da9645ea0b59b20e4f3111a5bc713c664afc3ec45af0`;
the accepted Web digest is
`sha256:e18f254d0c8ef68f980d5100c6f38e097bcf6b9c38b3044996ba78e7e95aa193`.
A fresh encrypted production backup passed a `100%` repository check. The
network-isolated rehearsal applied the exact candidate bundle, verified all 12
migrations and the new inbox schema, reapplied the bundle idempotently, and
started the retained v2.9 API read-only against the migrated copy.

Production preflight and the serialized migration passed, after which only API
and Web were recreated from the exact candidate digests. Eleven healthy
control-plane samples over 602 seconds and eight game-service continuity checks
over 1,045 seconds confirmed public health, release and schema identity, fresh
backup evidence, reachable A2S with zero bots, unchanged runtime continuity,
and zero open incidents or pending durable work. The existing Operator session
rendered the Events empty state with no mutation control, browser token, storage
entry, or console error. The inbox remained empty and the game-event agent,
spool import, and delivery remained disabled.

This acceptance did not repeat the OIDC matrix, submit a disruptive command,
run a multi-hour soak, or manufacture a gameplay event. It is short operational
evidence for a dormant candidate, not proof of long-term reliability, high
availability, production gameplay delivery, or an achieved SLO. Signed stable
tag `v2.10.0` targets revision
`c5bb47d56a6c11dbecb037ba90cdb154fe9fc3f6`. Stable workflow
[#34966981033](https://github.com/tov-vl/gold-src-ops/actions/runs/34966981033)
skipped both image builds, promoted the exact accepted candidate digests, and
independently smoke-tested both stable references before the GitHub Release was
published. Stable publication preserves the bounded dormant claim above; it
does not add production gameplay-delivery evidence.

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

- [GoldSrcOps v2.10.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.10.0)
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
