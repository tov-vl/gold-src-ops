# GoldSrcOps v2 Alert Delivery And Transactional Outbox

Status: accepted design baseline for the first v2 capability. The outbox schema,
transactional incident-alert enqueueing, and PostgreSQL claim state machine are
implemented; hosted dispatch and HTTP delivery remain disabled until the
following slices are complete.

## Scope

The first v2 capability delivers incident transition notifications to one
deployment-configured HTTP webhook. The transport is intentionally generic so
the reliability model can be proven before adding provider-specific Telegram,
Discord, or email adapters.

The capability covers two events:

- `server.availability.unavailable` when polling opens an availability incident
  after the configured failure threshold.
- `server.availability.recovered` when polling closes that incident after a
  successful query.

Repeated poll failures while an incident remains open do not create additional
unavailable events.

## Reliability Contract

Delivery is at least once. GoldSrcOps must not claim exactly-once delivery
because the remote endpoint can accept a request while the local caller times
out before observing the response.

The design guarantees that:

- every committed incident open or close transition has a matching committed
  outbox message;
- no outbox message exists for a rolled-back incident transition;
- concurrent dispatchers claim a message at most once at a time;
- an interrupted claim can be recovered after its lease expires;
- every retry carries the same stable event identifier and idempotency key.

The receiving webhook is responsible for deduplicating requests by the
`Idempotency-Key` header.

## Transaction Boundary

The polling use case currently tracks server state, a poll snapshot, and any
incident transition in one scoped EF Core `GoldSrcOpsDbContext`, then commits
them through one `SaveChangesAsync` call. The outbox row must be added to that
same change set before the commit.

The first implementation introduces:

- `IOutboxWriter` in Application for appending immutable event envelopes;
- `IOutboxStore` in Application for claim, completion, retry, and lease-recovery
  transitions;
- an explicit application `IUnitOfWork` for the cross-repository commit;
- EF Core outbox persistence in Infrastructure;
- an outbox dispatcher and an `IAlertDeliveryChannel` application boundary;
- an HTTP webhook adapter in Infrastructure.

The implementation must not use an EF Core save interceptor, reflection-based
domain-event discovery, or a second database commit for the outbox row. The
outbox record is an integration concern, not a Domain entity.

## Event Contract

Persist an immutable JSON snapshot rather than a reference that requires the
dispatcher to reconstruct historical state later. Do not persist CLR type
names.

Each event envelope contains:

| Field | Purpose |
| --- | --- |
| `eventId` | Stable identifier equal to the outbox message ID. |
| `eventType` | Stable name such as `server.availability.unavailable`. |
| `payloadVersion` | Explicit positive schema version, starting at `1`. |
| `occurredAtUtc` | Time of the incident transition. |
| `incidentId` | Availability incident identifier. |
| `serverId` | Registered server identifier. |
| `serverName` | Display name captured at transition time. |
| `reason` | Bounded failure or recovery context safe for operators. |
| `consecutiveFailures` | Failure count captured at transition time. |
| `openedAtUtc` | Incident start time. |
| `closedAtUtc` | Incident close time for recovery events, otherwise `null`. |
| `durationSeconds` | Incident duration for recovery events, otherwise `null`. |

Payloads must never contain RCON passwords, bearer tokens, secret references,
connection strings, or arbitrary RCON command content.

## Persistence Model

Add an `outbox_messages` table with the following logical columns. Final names
and lengths must follow the repository's existing EF Core conventions.

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `uuid` | Primary key and delivery idempotency key. |
| `EventType` | `varchar(128)` | Stable integration event name. |
| `PayloadVersion` | `smallint` | Positive schema version. |
| `AggregateType` | `varchar(64)` | `availability-incident` for this slice. |
| `AggregateId` | `uuid` | Incident identifier. |
| `OccurredAtUtc` | `timestamptz` | Ordering timestamp. |
| `Payload` | `jsonb` | Immutable serialized event envelope. |
| `Status` | `varchar(32)` | `Pending`, `Processing`, `Processed`, or `DeadLetter`. |
| `AttemptCount` | `integer` | Number of claims; never negative. |
| `NextAttemptAtUtc` | `timestamptz` | Earliest eligible claim time. |
| `ClaimId` | `uuid`, nullable | Token owned by the active dispatcher attempt. |
| `ClaimedAtUtc` | `timestamptz`, nullable | Start of the active claim lease. |
| `ProcessedAtUtc` | `timestamptz`, nullable | Successful completion time. |
| `LastError` | `varchar(2000)`, nullable | Sanitized diagnostic summary. |

Required constraints and indexes:

- unique `(EventType, AggregateId)` to prevent duplicate unavailable or
  recovered events for one incident;
- an active-ordering index on
  `(AggregateType, AggregateId, OccurredAtUtc, Id)`, filtered to pending and
  processing rows, so per-incident ordering does not require a table scan;
- a claim index starting with `(Status, NextAttemptAtUtc, OccurredAtUtc, Id)`,
  preferably filtered to pending rows;
- a recovery index on `(Status, ClaimedAtUtc)`, filtered to processing rows;
- a cleanup index on `(ProcessedAtUtc, Id)`, filtered to processed rows;
- check constraints for positive payload versions, nonnegative attempt counts,
  and status-dependent claim and completion fields.

## Claim And Completion Protocol

The PostgreSQL dispatcher follows the command queue's proven atomic-claim
shape, adapted for independent outbox messages:

1. Select one eligible pending row with `FOR UPDATE SKIP LOCKED`, while also
   preventing a later message for the same aggregate from overtaking an older
   pending or processing message.
2. In the same statement, set `Status=Processing`, assign a new `ClaimId`, set
   `ClaimedAtUtc`, and increment `AttemptCount`.
3. Commit the claim transaction before making the HTTP request.
4. Deliver outside any database transaction.
5. Mark success or failure only when `Id`, `Status=Processing`, and `ClaimId`
   still match the active attempt.

There is no global ordering guarantee. Ordering is preserved per availability
incident while an older message is pending or processing. `DeadLetter` is a
terminal state and does not block a later event; the resulting delivery gap is
observable and requires operator attention.

A recovery pass moves claims older than `ClaimTimeout` back to `Pending`,
clears their claim fields, and schedules a retry. This makes the dispatcher
safe for multiple workers and multiple application instances.

## Delivery And Retry Policy

Each application attempt sends exactly one HTTP POST. Do not layer automatic
`HttpClient` retries around the POST because the outbox owns retry accounting.

Retry these outcomes:

- connection failures and request timeouts;
- HTTP `408` and `429`;
- HTTP `5xx` responses.

Honor a valid bounded `Retry-After` value. Otherwise use exponential backoff
with jitter and configurable base and maximum delays. Other HTTP `4xx`
responses are permanent failures and move directly to `DeadLetter`. A message
also moves to `DeadLetter` after the configured maximum number of attempts.

The adapter must use a bounded request timeout and bounded response-body read,
must not follow redirects implicitly, and must not log request or response
bodies. Production configuration requires an HTTPS webhook URL.

## Configuration

The webhook endpoint and optional authorization value are deployment
configuration, not public API resources. Secrets must come from the deployment
secret provider and must never enter the database payload or logs.

The worker validates configuration at startup and exposes at least:

- `Enabled`;
- `LoopDelay`;
- `MaxConcurrency`;
- `ClaimTimeout`;
- `RequestTimeout`;
- `MaxAttempts`;
- base and maximum retry delays;
- processed-message retention period and cleanup batch size.

Each delivery attempt runs in its own dependency-injection scope, matching the
command dispatcher's scoped processing model.

## Observability And Health

OpenTelemetry metrics cover:

- messages enqueued;
- delivery attempts, successes, retries, and dead letters;
- stale claims recovered;
- delivery duration;
- pending count, oldest pending age, and dead-letter count;
- processed rows deleted by retention.

Structured logs include event, server, incident, attempt, and claim IDs plus a
sanitized HTTP status or failure category. They exclude payloads, bodies,
authorization values, and secret configuration.

A remote webhook outage must not fail application liveness or readiness and
cause a restart loop. Operators instead alert on backlog age, dead-letter
count, and repeated delivery failures.

## Retention And Operations

Processed messages are retained for a configurable default of 30 days and
deleted in bounded batches. Dead-letter messages are not deleted automatically
in the first slice.

Manual dead-letter replay and an administrative replay API are deferred. Until
they exist, recovery is an explicit database-assisted operational procedure
that must preserve the original event ID and payload.

## Rollout

1. Deploy an additive migration and transactional writer with the dispatcher
   disabled.
2. Apply the migration as the separate deployment action already required by
   `docs/deployment.md`.
3. Verify incident and outbox atomicity and observe enqueue metrics.
4. Configure the webhook secret and endpoint, then enable the dispatcher.
5. Monitor backlog age, retries, and dead letters before raising concurrency.

Rolling back to v1.1 leaves the additive table unused and stops creating new
messages. It does not remove queued messages. Re-enabling v2 resumes delivery
from persisted state.

## Verification Strategy

Implementation work is split into reviewable slices:

1. Add event contracts, EF Core mapping, migration, constraints, and a
   PostgreSQL migration test.
2. Add the writer and unit of work, then prove atomic incident/outbox commit,
   rollback behavior, and duplicate prevention in deterministic polling and
   PostgreSQL tests.
3. Add atomic claim, conditional completion, lease recovery, concurrency, and
   per-incident ordering tests against PostgreSQL.
4. Add the webhook adapter with synthetic HTTP-server tests for status
   classification, one request per attempt, stable idempotency headers,
   timeouts, and bounded response handling.
5. Add the hosted worker, validated options, metrics, log-safety tests, and
   bounded retention cleanup.
6. Update deployment and operations documentation, then run the full quality
   gate and production container smoke test.

## Explicit Deferrals

- RabbitMQ or another broker.
- Service extraction.
- Provider-specific notification channels.
- Public subscription or notification-preference APIs.
- Multi-tenant routing.
- An alert-management UI.
- A dead-letter replay API.
- Distributed polling claims; the singleton polling constraint remains.
