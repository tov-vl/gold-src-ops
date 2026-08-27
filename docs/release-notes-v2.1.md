# GoldSrcOps v2.1.0 Release Notes

Prepared: 2026-08-27. Published: 2026-08-27. Status: released.

## Overview

GoldSrcOps v2.1 adds a bounded operator workflow for inspecting and replaying
dead-letter alert events without direct PostgreSQL mutation. It preserves the
original event identity and immutable payload, records every accepted replay in
durable audit storage, and makes ambiguous client retries safe through an
explicit idempotency key.

This is a backward-compatible minor release over v2.0.0. Existing API,
authentication, alert-delivery, container, and webhook contracts remain in
effect. The release adds four endpoints, two additive migrations, an operator
recovery workflow, and replay-specific telemetry.

## Included In v2.1

- Bounded `Reader` dead-letter listing with opaque cursor pagination and stable
  newest-first ordering.
- `Reader` detail inspection with the immutable payload, sanitized delivery
  failure, replay count, and a warning about newer retained events.
- Single-message `Operator` replay with a UUID `Idempotency-Key`, mandatory
  bounded reason, and audit identity derived from the validated JWT `sub` claim.
- Durable `Reader` lookup for accepted replay requests so an ambiguous HTTP
  result can be resolved without creating a new replay cycle.
- Atomic PostgreSQL persistence that records the replay audit and requeues the
  existing outbox event in one transaction.
- Stable event identity, payload, payload version, aggregate identity, and
  occurrence time across every replay cycle.
- PostgreSQL row locking, conditional state transitions, and unique constraints
  for same-key retries, distinct-key races, and ordering against newer events.
- Low-cardinality replay outcome metrics and sanitized lifecycle logs with
  explicit ambiguous and faulted outcomes.
- Updated alert-delivery recovery guidance for review, replay, observation, and
  safe retry after an uncertain client result.

## HTTP Contract

| Endpoint | Policy | Purpose |
| --- | --- | --- |
| `GET /api/alert-delivery/dead-letters` | `Reader` | List current dead-letter messages without payloads. |
| `GET /api/alert-delivery/dead-letters/{eventId}` | `Reader` | Inspect one dead letter and its immutable payload. |
| `POST /api/alert-delivery/dead-letters/{eventId}/replay` | `Operator` | Requeue one reviewed dead letter. |
| `GET /api/alert-delivery/replays/{requestId}` | `Reader` | Read the durable result of an accepted replay. |

Replay acceptance returns `202 Accepted` and means the existing event became
eligible for a new bounded delivery cycle. It does not mean the webhook already
accepted the event. Repeating identical intent with the same key returns the
stored result; reusing the key for different intent returns `409 Conflict`.

## Compatibility And Migration

- Existing v1 and v2 REST endpoints, JWT bearer authentication, `Reader` and
  `Operator` policies, alert payloads, and webhook delivery semantics are
  unchanged.
- `AddOutboxReplayPersistence` adds nullable dead-letter time, a replay count,
  the append-only replay audit table, constraints, and an initial partial index.
- `AlignDeadLetterListIndex` replaces that index with the complete cursor order.
  Both migrations are additive and must remain in their recorded order.
- Legacy dead letters keep a null timestamp rather than receiving an invented
  backfill value. The list contract orders them after known dead-letter times.
- Both normal index builds and initial constraint validation can block writes to
  `outbox_messages`. Inspect table size and apply migrations as one serialized,
  low-traffic deployment action before starting v2.1 instances.
- Application rollback to v2.0.0 leaves the additive columns and replay audit
  table unused while ordinary alert dispatch continues. Do not down-migrate
  while replay audit or retained outbox events may still be required.
- Replay audit is append only and is not part of processed-message cleanup. A
  retention policy remains deferred until an operational or regulatory
  requirement exists.

## Security And Audit

Read endpoints require the existing `Reader` policy, which also accepts an
`Operator`. Replay requires `Operator`. The server derives `RequestedBy` from
the validated subject claim; request data cannot override the audit identity.

The replay reason is bounded operational audit data and must not contain
credentials, tokens, secret-bearing URLs, response bodies, or unrelated
personal data. Metrics and logs exclude subjects, reasons, payloads, previous
errors, principal claims, webhook URLs, authorization values, and exception
messages. Durable replay storage remains the source for who requested the
mutation and why.

## Verified Release Baseline

The 2026-08-27 local candidate gate completed with .NET SDK `10.0.400`, Docker
Engine `29.7.2`, and PostgreSQL `16-alpine`:

- `239/239` tests passed with zero skips;
- Release build completed with zero warnings and zero errors;
- formatting verification produced no changes;
- NuGet Audit and the vulnerable-package report found no known vulnerable
  direct or transitive package;
- every migration applied to a clean PostgreSQL database as a separate action;
- the production container smoke passed image hardening, configuration
  fail-fast, migration, dispatcher registration, liveness, and readiness checks.

Design pull request #12 and implementation pull requests #13 through #16 passed
the required `Quality Gate` and `Container Smoke` checks. Their protected-main
merge commits and genuine post-merge runs also passed. Final release-candidate
pull request #17, post-merge `main` run #57, and signed-tag run #58 passed the
same jobs before publication. The signed release revision is `af7c2f4`.

Detailed evidence and accepted boundaries are recorded in
[docs/v2.1-readiness.md](v2.1-readiness.md).

## Deployment Notes

Apply both replay migrations after the v2.0.0 outbox migration and before
starting v2.1 instances. Run them as a serialized low-traffic deployment action
with application write traffic controlled; disabling the dispatcher alone does
not stop polling from enqueueing new outbox rows. Then restore the existing
webhook configuration and observe pending, dead-letter, oldest-message-age,
delivery outcome, and replay outcome metrics.

For a dead letter, correct the receiver condition first, inspect the event and
newer-event warning, submit one replay with a fresh key, and follow the durable
replay record until delivery reaches its next terminal state. Reuse the original
key after an ambiguous response. Do not create a replacement event, edit the
payload, or reset outbox fields through routine SQL.

## Intentional Limits

- Delivery remains at least once. Receivers must deduplicate by the stable event
  ID and idempotency key.
- Replay is limited to one reviewed message per request. Bulk, scheduled, and
  automatic replay remain deferred.
- There is no management UI, payload editor, dead-letter purge API, or replay
  audit retention policy.
- A newer processed event is a warning, not an automatic replay block. The
  operator owns the ordering decision after reviewing receiver state.
- The production container smoke does not contact a real external webhook.
  Target TLS, identity, secret injection, receiver capacity, and backup restore
  require deployment evidence.
- A second channel, broker, distributed polling claim, service extraction, and
  automatic image publication remain deferred.

## Release References

- Readiness evidence: [docs/v2.1-readiness.md](v2.1-readiness.md)
- Replay design: [docs/dead-letter-replay.md](dead-letter-replay.md)
- Alert delivery operations: [docs/alert-delivery.md](alert-delivery.md)
- Previous release: [docs/release-notes-v2.md](release-notes-v2.md)
- Outbox design: [docs/v2-alert-outbox.md](v2-alert-outbox.md)
- Deployment contract: [docs/deployment.md](deployment.md)
- Security model: [docs/security.md](security.md)
- Full smoke test: [docs/smoke-test.md](smoke-test.md)

## Publication Status

Published on 2026-08-27 as the
[GoldSrcOps v2.1.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.1.0)
from signed annotated tag `v2.1.0`. Final candidate pull request #17, the
[post-merge `main` run](https://github.com/tov-vl/gold-src-ops/actions/runs/33107854017),
and the
[tag-triggered run](https://github.com/tov-vl/gold-src-ops/actions/runs/33110060679)
all passed `Quality Gate` and `Container Smoke` on release revision `af7c2f4`.
