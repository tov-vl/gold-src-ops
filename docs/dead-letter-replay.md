# Dead-Letter Inspection And Replay Design

Status: implementation in progress. Replay metadata, append-only audit
persistence, the additive migrations, bounded `Reader` inspection, the
transactional `Operator` replay endpoint, and durable replay-record reads are
implemented. Replay-specific telemetry, sanitized lifecycle logs, and final
release-gate verification remain.

Decision date: 2026-08-26.

## Problem

The alert dispatcher moves a message to `DeadLetter` after a permanent delivery
failure or after the bounded retry budget is exhausted. The message remains
durable and observable, but the dispatcher no longer claims it automatically.

A later event for the same incident may still be delivered. This prevents one
terminal failure from blocking the outbox, but it can leave a gap at the
receiver. Recovering that gap currently requires direct PostgreSQL access and a
careful manual state change.

## Goals

- Let a `Reader` inspect dead-letter messages without database access.
- Let an `Operator` requeue one reviewed message at a time.
- Preserve the original event ID, payload, payload version, aggregate identity,
  and occurrence time.
- Make an ambiguous client retry safe through an explicit idempotency key.
- Allow only one state transition when operators submit concurrent replay
  requests.
- Persist who requested each accepted replay, when, and why in the same
  transaction as the outbox state change.
- Keep payloads, failure details, reasons, and principal claims out of logs and
  metric dimensions.

## Non-Goals

- Editing or replacing the immutable event payload.
- Creating a new event ID for an existing incident transition.
- Bulk replay, scheduled replay, or automatic replay of every dead letter.
- Purging dead letters or audit history through the API.
- Adding a second delivery channel, broker, service boundary, or management UI.
- Providing exactly-once delivery. The receiver must still deduplicate by the
  stable event ID.

## Operator Workflow

1. An alert on `goldsrcops.alerts.dead_letter_count` identifies that operator
   attention is required.
2. A `Reader` lists dead letters and opens one detail record without direct
   database access.
3. The operator verifies the receiver, credentials, routing, and any newer
   event before deciding whether another delivery is safe.
4. An `Operator` submits one replay with a new UUID idempotency key and a
   concise reason that records the corrected condition.
5. If the HTTP result is ambiguous, the client repeats the exact request with
   the same key. It does not generate another key merely because the response
   was lost.
6. The client follows the replay-record `Location` and the operator observes
   delivery and backlog telemetry. Replay acceptance does not mean the webhook
   has already accepted the event.
7. If the new bounded delivery cycle also reaches `DeadLetter`, another replay
   requires a new review, reason, and idempotency key.

## HTTP Contract

The first implementation adds an `Alert Delivery` endpoint group.

| Endpoint | Policy | Purpose |
| --- | --- | --- |
| `GET /api/alert-delivery/dead-letters` | `Reader` | List current dead-letter messages. |
| `GET /api/alert-delivery/dead-letters/{eventId}` | `Reader` | Inspect one current dead letter and its immutable payload. |
| `POST /api/alert-delivery/dead-letters/{eventId}/replay` | `Operator` | Requeue one current dead letter. |
| `GET /api/alert-delivery/replays/{requestId}` | `Reader` | Read the durable result of an accepted replay request. |

All four endpoints are implemented. Replay-specific telemetry and final
operational verification remain part of the next implementation slice.

The list uses opaque cursor pagination ordered by `DeadLetteredAtUtc` descending,
then `OccurredAtUtc` descending, then `Id` descending. A missing legacy
`DeadLetteredAtUtc` sorts after known values. `limit` defaults to 50 and accepts
values from 1 through 200. The cursor is an API implementation detail and must
not expose SQL or permit clients to construct arbitrary predicates.

List items omit the payload. A detail response includes the exact persisted
JSON payload because an operator must review what will be sent. Alert payload
contracts remain responsible for excluding secrets. The response also includes:

- event ID, type, payload version, and occurrence time;
- aggregate type and aggregate ID;
- attempt count, replay count, and dead-letter time;
- the bounded, sanitized last error;
- whether a newer retained event exists for the same aggregate, its current
  delivery status, and the latest known occurrence time.

The newer-event indicator is a warning, not an automatic block. A replayed
unavailable event may arrive after a recovered event that was already delivered.
The API never rewrites event timestamps to hide that ordering gap.

### Replay Request

The client supplies a UUID in the `Idempotency-Key` header and a mandatory,
trimmed reason of 1 through 500 characters:

```http
POST /api/alert-delivery/dead-letters/{eventId}/replay
Idempotency-Key: 68d79e5b-2dc0-43d8-8fd8-650428e2be91
Content-Type: application/json

{
  "reason": "Receiver authorization was corrected."
}
```

The reason is operational audit data. It must not contain credentials, tokens,
webhook URLs with secrets, response bodies, or unrelated personal data.

An accepted request returns `202 Accepted`, a `Location` header for the replay
record, and a response containing:

- replay request ID and event ID;
- `requestedBy`, `requestedAtUtc`, and the reason;
- the assigned replay number;
- the previous attempt count and dead-letter time;
- `status=Pending` and `nextAttemptAtUtc`.

Repeating the same request ID for the same event, principal, and normalized
reason returns the original accepted result without changing the message again.
Reusing the key for different intent returns `409 Conflict`.

| Outcome | HTTP response | Problem code |
| --- | --- | --- |
| Replay accepted or an identical request repeated | `202 Accepted` | none |
| Missing or invalid idempotency key or reason | `400 Bad Request` | `alert_delivery.replay_invalid` |
| Missing or invalid token | `401 Unauthorized` | bearer authentication response |
| Reader attempts replay | `403 Forbidden` | authorization response |
| Event does not exist or is no longer retained | `404 Not Found` | `alert_delivery.event_not_found` |
| Replay record does not exist | `404 Not Found` | `alert_delivery.replay_not_found` |
| Event exists but is not `DeadLetter` | `409 Conflict` | `alert_delivery.event_not_dead_letter` |
| A newer event for the aggregate is already `Processing` | `409 Conflict` | `alert_delivery.newer_event_processing` |
| Idempotency key was used for different intent | `409 Conflict` | `alert_delivery.idempotency_conflict` |
| Event has no supported source serialization boundary | `409 Conflict` | `alert_delivery.event_not_replayable` |

Problem responses use stable machine-readable codes and do not echo payloads,
reasons, exception messages, authorization values, or webhook configuration.

## Authorization And Audit Identity

The existing JWT bearer trust boundary remains unchanged. Read endpoints use
the `Reader` policy, which also accepts `Operator`. Replay uses the `Operator`
policy and derives `RequestedBy` from the validated `sub` claim through the same
principal helper used by RCON commands. The request body cannot supply or
override the audit identity.

The fallback `Operator` policy still protects any endpoint that is accidentally
mapped without an explicit policy. API integration tests must nevertheless
verify every route in the endpoint policy matrix.

## Implementation Boundaries

- Contracts define the bounded list, detail, replay request, accepted response,
  and problem shapes without exposing EF Core entities.
- API endpoints apply policies, obtain the validated subject, validate transport
  input, and map application results to typed HTTP responses.
- Application services own inspection and replay orchestration through explicit
  query and mutation interfaces. They do not depend on EF Core or Npgsql.
- Infrastructure owns projections, row locks, the PostgreSQL transaction, and
  audit persistence.
- The outbox and replay audit remain integration concerns. No alert-delivery
  entity is added to the Domain project merely to mirror database tables.

## Persistence Design

An additive migration extends `outbox_messages` with:

| Column | Purpose |
| --- | --- |
| `DeadLetteredAtUtc` | Nullable timestamp set by every new transition to `DeadLetter`. Legacy rows may remain null. |
| `ReplayCount` | Non-negative number of accepted replay cycles, initially zero. |

A new check constraint ensures that only `DeadLetter` rows may have a non-null
`DeadLetteredAtUtc`; the already-applied status constraint is not edited. A
partial index supports the dead-letter list ordering. Existing rows are not
assigned an invented dead-letter timestamp during backfill.

The migration also adds an append-only `outbox_replay_requests` table:

| Column | Purpose |
| --- | --- |
| `Id` | Client-supplied UUID idempotency key and primary key. |
| `OutboxMessageId` | Stable event ID that was requeued. |
| `EventType`, `PayloadVersion` | Event contract metadata copied for durable audit. |
| `AggregateType`, `AggregateId` | Aggregate identity copied for durable audit. |
| `OccurredAtUtc` | Original event occurrence time. |
| `RequestedBy`, `RequestedAtUtc` | Validated subject and server timestamp. |
| `Reason` | Bounded operator justification. |
| `ReplayNumber` | Monotonic replay cycle for the event. |
| `PreviousAttemptCount` | Retry budget consumed by the dead-letter cycle. |
| `PreviousDeadLetteredAtUtc` | Dead-letter timestamp, nullable for legacy rows. |
| `PreviousLastError` | Bounded sanitized failure retained before it is cleared. |
| `NextAttemptAtUtc` | Time at which the accepted replay became eligible. |

`Id` is unique globally, and `(OutboxMessageId, ReplayNumber)` is unique per
event. An index on `(OutboxMessageId, RequestedAtUtc)` supports audit lookup.
The table intentionally stores no payload, claim token, webhook URL, response
body, authorization value, or access token.

There is no cascading foreign key from replay audit to the outbox row. Processed
outbox cleanup may eventually delete the event, while the audit record must
remain attributable by its stable event ID. Replay audit rows are not included
in the existing 30-day processed-message cleanup; a separate retention policy
can be introduced when an operational or regulatory requirement exists.

The persistence migration runs in one transaction and builds the initial
partial dead-letter index normally. A follow-up additive migration recreates
that index with the complete list order: `DeadLetteredAtUtc`, `OccurredAtUtc`,
then `Id`, all descending with nulls last. PostgreSQL can block writes to
`outbox_messages` while either normal index build runs and while the initial
constraints are validated. Inspect the table size and apply both migrations
during a low-traffic rollout. If the table becomes large, add a later rollout
migration using concurrent index creation and staged constraint validation; do
not edit either applied migration.

## Atomic State Transition

An accepted replay uses one PostgreSQL transaction for the audit insert and
the outbox update. The application service and repository boundary make this
transaction explicit.

The transition changes only these mutable delivery fields:

- `Status`: `DeadLetter` to `Pending`;
- `AttemptCount`: reset to zero for one new bounded retry cycle;
- `ReplayCount`: increment by one;
- `NextAttemptAtUtc`: set from the server UTC clock;
- `DeadLetteredAtUtc`, `LastError`, `ClaimId`, `ClaimedAtUtc`, and
  `ProcessedAtUtc`: cleared.

Resetting `AttemptCount` is deliberate rather than a blind database edit. The
previous count, error, and dead-letter timestamp are first copied to the audit
record. `Id`, `EventType`, `PayloadVersion`, `AggregateType`, `AggregateId`,
`OccurredAtUtc`, and `Payload` never change.

The dispatcher cannot observe `Pending` until the audit record commits. If
delivery is disabled, the replay remains pending. If it is enabled, a worker
may claim the event immediately after commit; cancelling the HTTP request after
commit does not revoke the accepted replay.

Locking existing outbox rows alone is insufficient because a concurrent
incident recovery could insert a newer event after the replay query. For the
current incident-alert contracts, the replay transaction first locks the source
availability-incident row. Incident close and recovered-event insertion already
share one unit of work that updates this row, so the lock serializes replay with
creation of a newer event. Any future replayable aggregate type must define an
equivalent source-of-truth serialization rule.

The transaction then locks the target outbox row and newer active rows for the
same aggregate in deterministic `(OccurredAtUtc, Id)` order. A newer `Pending`
row stays locked until the older dead letter becomes `Pending`; after commit the
existing claim query keeps that newer row behind the replayed event. If a newer
row is already `Processing`, replay returns `409` and the operator retries after
that attempt reaches a terminal or pending state. A newer event that is already
`Processed` cannot be undone; the detail response warns about it and replay
remains an explicit operator decision.

## Idempotency And Concurrency

- The replay request ID is protected by a database primary key, not an
  in-memory cache.
- An existing request ID with identical intent returns its stored result even
  if the outbox message has since moved to `Processing` or `Processed`.
- The outbox update is conditional on `Id` and `Status=DeadLetter`.
- Two requests with different keys can race, but only one transition updates
  the row and creates an accepted audit record. The loser returns `409`.
- Two concurrent requests with the same key converge on the one committed
  audit record. A unique-key race is resolved by rereading that record and
  applying the same-intent check.
- Multiple application instances use the same PostgreSQL invariants; no
  process-local lock is part of correctness.

## Observability

Add a low-cardinality counter for replay requests with `accepted`,
`idempotent`, `conflict`, and `invalid` results. Existing pending and dead-letter
gauges reflect the state transition without additional per-event instruments.

Structured logs include replay request ID, event ID, replay number, and outcome.
They exclude payloads, reasons, previous errors, principal claims, exception
messages, webhook URLs, and authorization values. The durable audit table, not
logs, is the source for who requested the mutation and why.

## Verification Strategy

Implementation is split into reviewable slices:

1. Completed: add the schema fields, constraints, indexes, audit table, and
   migrations.
   Verify clean migration, legacy nullable timestamps, and processed cleanup
   that leaves replay audit intact against PostgreSQL.
2. Completed: add bounded read models and `Reader` endpoints. Verify cursor
   stability, payload omission from lists, detail projection, newer-event
   warning, auth, and response bounds through API and PostgreSQL integration
   tests.
3. Completed: add the transactional replay service, `Operator` endpoint, and
   durable replay-record endpoint. PostgreSQL tests verify atomic audit/state
   commit, rollback, attempt reset, immutable event fields, same-key retries,
   key reuse conflicts across events, distinct-key races, a newer processing
   event, concurrent incident close, aggregate ordering, and dispatcher pickup.
   API tests verify the `Reader`/`Operator` policy boundary.
4. Add replay outcome metrics, sanitized lifecycle logs, and final operations
   guidance. Run the full quality gate and production container smoke.

No implementation slice may edit the already-applied v2 outbox migration. The
schema change requires a new additive migration whose generated SQL is reviewed
for locks, table rewrites, and index build behavior before rollout.

## Alternatives Considered

- Continue using manual SQL as the normal workflow. Rejected because it bypasses
  authorization, request idempotency, validation, and durable audit.
- Insert a replacement outbox row. Rejected because a new event ID weakens
  receiver deduplication and misrepresents one incident transition as two.
- Automatically replay every dead letter. Rejected because permanent failures
  can create an unbounded retry loop and repeated external side effects.
- Add bulk replay first. Deferred until single-message safety and a concrete
  operational volume justify the larger blast radius.
- Add a second delivery channel first. Deferred until its receiver and routing
  requirements are concrete; it does not itself repair failed delivery through
  the existing channel.
