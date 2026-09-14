# Game Event Ingestion

This document defines contract version 1 of the local v2.10 game-events
foundation. It is an API and persistence contract, not evidence that an agent
has been installed or that production accepts gameplay events.

## Endpoint And Identity

`POST /api/servers/{serverId}/game-events` requires a JWT access token for the
GoldSrcOps API audience with all of the following claims:

- a stable `sub`;
- `permissions` containing `ingest:game-events`;
- exactly one `https://goldsrcops.com/claims/server_id` containing the canonical
  server UUID used in the route.

The machine policy is separate from `Reader` and `Operator`. A human token
cannot ingest events, and a writer token cannot call human read or mutation
endpoints.

## Version 1 Contract

```json
{
  "contractVersion": 1,
  "eventId": "527ee0bb-0102-44b2-82f7-7e1a3ed10db1",
  "sourceInstanceId": "680fd9c5-84af-41d7-8660-ed5cb8728345",
  "sequenceNumber": 42,
  "type": "round.ended",
  "occurredAtUtc": "2026-09-14T09:00:00Z",
  "map": "de_dust2",
  "players": 12,
  "bots": 0
}
```

Allowed `type` values are:

- `server.started`;
- `server.stopped`;
- `map.started`;
- `round.started`;
- `round.ended`.

`map` is required for map and round events. It is at most 128 ASCII letters,
digits, underscores, or hyphens and must begin and end with a letter or digit.
`players` and `bots` must either both be absent or both be present. Counts are
between 0 and 255, and bots cannot exceed players. `occurredAtUtc` cannot be
more than five minutes ahead of API receive time. The request body cannot
exceed 4 KiB.

The contract deliberately has no player identifier, player name, IP address,
chat text, command output, provider metadata, or arbitrary extension object.

## Delivery Semantics

The sender must durably create `eventId`, `sourceInstanceId`, and the monotonic
positive `sequenceNumber` before its first delivery attempt. It may retry the
same bytes after an ambiguous transport result.

| Outcome | Meaning |
| --- | --- |
| `202 Accepted` | The event was persisted for the first time. |
| `200 OK` | The same event ID and canonical intent were already persisted. |
| `400 Bad Request` | The version or bounded payload is invalid. |
| `403 Forbidden` | The machine permission or server binding is invalid. |
| `404 Not Found` | The bound server is not registered. |
| `409 Conflict` | An event ID or source sequence was reused for different content. |
| `413 Payload Too Large` | The request body exceeds 4 KiB. |

Both successful responses return the original persisted receipt. `duplicate`
is `false` for `202` and `true` for an idempotent `200`. Conflict responses use
the stable codes `game_event.event_id_conflict` and
`game_event.source_sequence_conflict` and do not echo the submitted payload.
Idempotency is guaranteed for the configured inbox-retention window; a sender
must not retry an event after that window and assume the old receipt remains.

## Persistence And Retention

PostgreSQL stores one immutable inbox row per event. The primary key is the
event ID. A unique `(ServerId, SourceInstanceId, SequenceNumber)` index enforces
one use of each source sequence position under concurrent delivery. The
canonical SHA-256 intent hash
uses an explicitly ordered representation and allows a retry to be
distinguished from conflicting event-ID reuse across application versions.

Retention uses `ReceivedAtUtc`, not the sender-controlled occurrence time. The
default period is 45 days; each five-minute cleanup pass deletes at most 1,000
of the oldest expired rows. Configuration bounds are 1 to 3,650 days, 10 to
86,400 seconds between passes, and 1 to 10,000 rows per batch.

Metrics use only allowlisted event type and result labels:

- `goldsrcops.game_events.ingestion_requests`;
- `goldsrcops.game_events.retention_runs`;
- `goldsrcops.game_events.deleted`;
- `goldsrcops.game_events.retention_duration`.

## Deferred Work

The next slice may implement a sandboxed AMX Mod X/ReAPI sender with a local
durable queue and bounded retry policy. Auth0 M2M provisioning, production
migration and deployment, game-host installation, and any Reader projection
require their own review and acceptance evidence. A broker is deferred until
observed load or ownership pressure justifies it.
