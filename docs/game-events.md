# Game Event Ingestion

This document defines contract version 1 of the local v2.10 game-events
foundation, sandbox companion agent, and local file-spool IPC. None is evidence
that an agent or plugin has been installed or that production accepts gameplay
events.

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

## Sandbox Companion Agent

`GoldSrcOps.GameEventAgent` is a separate .NET Worker process. It does not run
inside the API or game server, and delivery is disabled by default. A direct
test source accepts one bounded JSON file and creates the event ID, persistent
source-instance ID, monotonic sequence, and exact request bytes inside one
SQLite transaction:

```powershell
dotnet run --project src/GoldSrcOps.GameEventAgent -- enqueue --file "$PWD/samples/game-event-agent/round-ended.json"
dotnet run --project src/GoldSrcOps.GameEventAgent -- status
```

## Local File-Spool IPC

The third local slice separates the game plugin from the companion
agent with a durable, bounded file spool. The producer contract is a strict
JSON envelope no larger than 4 KiB:

```json
{
  "spoolVersion": 1,
  "recordId": "77a456f5-e111-4736-a010-649c60e36bc0",
  "event": {
    "type": "round.ended",
    "occurredAtUtc": "2026-09-14T09:00:00Z",
    "map": "de_dust2",
    "players": 12,
    "bots": 0
  }
}
```

The file name must be the canonical lower-case `<recordId>.json`. Unknown JSON
members, unsupported versions, mismatched names, invalid source events, empty
records, and records over 4 KiB are rejected. The contract contains no OAuth
material, player identity, address, chat text, arbitrary JSON, or RCON data.

A producer writes `<recordId>.tmp`, flushes the complete file, and renames it
within `incoming` to `<recordId>.json`. The importer ignores `.tmp` files and
uses these states:

1. Move `incoming/<recordId>.json` to `processing` to claim it.
2. Insert the normalized event and an exact-byte SHA-256 receipt in the same
   SQLite transaction.
3. Move the claimed file to `accepted` after that transaction commits.
4. Remove the receipt and then delete the accepted file.

After interruption, a `processing` record with the same receipt is reconciled
without allocating another event or sequence. An `accepted` record is only
finalized; it is never enqueued again. Reusing one record ID with different
bytes or supplying an invalid record moves the file to `rejected`. A full
queue, exhausted receipt capacity, temporary SQLite contention, or a transient
filesystem failure leaves the record retryable and reports it as deferred.
Each import batch is bounded.

On Unix, spool directories are set to owner-only `0700` and files created by
the reference writer use `0600`. Reparse-point directories and records fail
closed. Windows deployments must supply an equivalently restricted directory
ACL. Logs and status expose only aggregate state. A healthy completed batch
normally leaves `spool-receipts=0`.

Exercise this boundary locally without enabling HTTP delivery:

```powershell
dotnet run --project src/GoldSrcOps.GameEventAgent -- spool-write --file "$PWD/samples/game-event-agent/round-ended.json"
dotnet run --project src/GoldSrcOps.GameEventAgent -- import-spool
dotnet run --project src/GoldSrcOps.GameEventAgent -- status
```

For a continuous spool-only sandbox process, set
`GameEventAgent__Spool__Enabled=true` before `run`. Root path, one-to-sixty
second import interval, and one-to-one-thousand record batch size are bounded
configuration. `run` fails when both spool import and HTTP delivery are
disabled.

The SQLite queue uses WAL mode and a finite capacity. A dispatcher claims one
event with a lease, so a process failure leaves it available for the same-byte
retry after the lease expires. `202` and matching `200` receipts remove the
entry. Conflicts, invalid requests, and invalid success receipts enter durable
dead-letter state. Network errors, timeouts, throttling, server errors, token
failures, forbidden bindings, and missing server registration use capped
exponential retry; the attempt and event-age limits eventually dead-letter the
entry rather than retry beyond the API idempotency-retention horizon.

The Worker obtains OAuth tokens with the client-credentials grant. Configuration
contains only `ClientSecretFile`; the secret itself must be stored outside the
repository in an owner-readable file (`0600` on Unix). Remote cleartext HTTP endpoints,
redirects, oversized responses, incomplete receipt identities, and incomplete
delivery configuration fail closed. Logs and `status` contain aggregate queue
outcomes, never tokens, secret contents, request bodies, map names, or player
counts.

`status --json` emits the same aggregate information as a versioned,
machine-readable snapshot. Schema version 1 contains the spool-import and
delivery gates, configured queue capacity, queue counts, spool counts, and the
next sequence number. It deliberately omits the source-instance ID, server ID,
OAuth configuration, paths, payloads, map names, and player counts. Host-side
automation may parse this snapshot, but it must still distinguish transient
`ready`, `processing`, `accepted`, `temporary`, and in-flight work from durable
`rejected` or dead-letter evidence instead of reducing every non-zero count to
one health bit.

To run a sandbox delivery loop, provide these environment-backed configuration
values before invoking `run`:

- `GameEventAgent__Delivery__Enabled=true`;
- `GameEventAgent__Delivery__ServerId`;
- `GameEventAgent__Delivery__ApiBaseUrl`;
- `GameEventAgent__Delivery__OAuth__TokenEndpoint`;
- `GameEventAgent__Delivery__OAuth__ClientId`;
- `GameEventAgent__Delivery__OAuth__ClientSecretFile`;
- `GameEventAgent__Delivery__OAuth__Audience`.

The API base URL and token endpoint must use HTTPS, except that loopback HTTP is
allowed for a local synthetic server. Queue path, capacity, spool interval and
batch size, dispatch interval, lease, retry, request-timeout, maximum-attempt,
and maximum-event-age settings have bounded defaults in `appsettings.json`.

## Sandbox AMX Mod X/ReAPI Producer

`samples/amxmodx-game-event-producer/goldsrcops_game_events.sma` implements the
producer side of spool version 1 for one event type, `round.ended`. It is
disabled by default and observes post-call `RG_RoundEnd` hooks while excluding
setup and restart pseudo-rounds. It emits at most one successful record before
the next `RG_CSGameRules_RestartRound`.

Before creating a record, the producer enumerates the dedicated incoming
directory and counts every non-directory entry, including incomplete `.tmp`
files and unrecognized files. `goldsrcops_spool_max_pending` defaults to 1,000
and accepts only values from 1 through 10,000. An unavailable directory,
invalid bound, failed enumeration, or count at the configured limit suppresses
the new record and increments the existing aggregate failure counter. This is
an intake backpressure boundary, not permission to delete queued evidence.

The record contains the current map plus aggregate connected player and bot
counts; HLTV is excluded. Map names are validated against the API allowlist,
the UUID v4 record ID is lowercase and canonical, and UTC is derived directly
from Unix time rather than the host timezone. The configured `incoming` path
must already exist, be relative to the mod directory, and contain no traversal
or unsupported characters.

Publication uses `<recordId>.tmp`, owner-only `0600` permissions, a buffered
flush and close, an exact byte-count check, and same-directory rename to the
canonical `<recordId>.json`. The plugin performs no network access and has no
OAuth, HTTP retry, player identity, address, chat, RCON, or provider surface.
Only aggregate emitted, failed, and ignored counts are exposed by the
`goldsrcops_events_status` server command.

Compile the source without installing it:

```powershell
pwsh -NoProfile -File ./tools/smoke/amxx-game-event-producer.ps1
```

The compile smoke pins and verifies AMX Mod X `1.10.0.5481` and ReAPI
`5.24.0.300`; output stays below ignored `artifacts/`. A producer-shaped fixture
is parsed by the same strict .NET spool contract in unit tests. AMX Mod X does
not expose a portable filesystem sync primitive, so buffered flush and close
prove complete-file process handoff but not survival across host power loss.

## Reader Projection

`GET /api/servers/{serverId}/game-events?limit=` is a bounded human read model.
It requires the existing Reader policy, accepts a limit from 1 through 100
(default 50), and returns only retained `round.ended` records for the selected
server, ordered by occurrence time and event ID descending. The response
contains event type, occurrence time, map, and aggregate player/bot counts.

The response deliberately omits event and source IDs, source sequence,
contract version, receive time, intent hash, and raw inbox state. The Blazor
route `/operator/servers/{serverId}/events` renders the same projection without
mutation controls. This read path uses the existing inbox index and adds no
schema migration. Local fixture and PostgreSQL tests prove query behavior, not
production ingestion or host installation.

## Deferred Work

Auth0 M2M provisioning, production migration and deployment, game-host
activation, production event delivery, and dead-letter replay require their
own review and acceptance evidence. In particular, the sandbox compile,
fixture, and Reader projection do not prove host compatibility, directory
ownership, power-loss durability, or successful gameplay delivery. A broker is
deferred until observed load or ownership pressure justifies it. The dormant
candidate gate and delivery-pilot boundary are defined in
[v2.10 release readiness](v2.10-readiness.md). The v2.11
[pilot-readiness bundle and installer](v2.11-game-event-pilot.md) make the
component set reproducible and permit a default-off side-by-side installation;
they still do not provision identity, modify the live game tree, activate a
producer, or establish a production gameplay claim.
