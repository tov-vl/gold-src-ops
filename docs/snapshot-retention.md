# Snapshot Retention

GoldSrcOps removes expired `PollSnapshot` rows through a bounded background
cleanup. Current server state and availability incidents have independent
tables and are never part of the retention delete.

## Configuration

Configuration lives under `SnapshotRetention`:

```json
{
  "SnapshotRetention": {
    "Enabled": true,
    "RetentionDays": 30,
    "CleanupIntervalSeconds": 300,
    "BatchSize": 1000
  }
}
```

Validated values:

| Key | Default | Valid range |
| --- | ---: | ---: |
| `Enabled` | `true` | `true` or `false` |
| `RetentionDays` | `30` | 1 to 3650 days |
| `CleanupIntervalSeconds` | `300` | 10 to 86400 seconds |
| `BatchSize` | `1000` | 1 to 10000 rows |

An explicitly configured invalid value fails application startup. Environment
variables use the standard double-underscore form, for example
`SnapshotRetention__RetentionDays=90`.

## Cleanup Semantics

The worker performs one cleanup pass immediately after startup and then waits
for `CleanupIntervalSeconds` between passes. A pass:

1. Calculates `cutoff = UtcNow - RetentionDays`.
2. Selects the oldest rows where `CheckedAtUtc < cutoff`, ordered by
   `CheckedAtUtc` and `Id`.
3. Deletes at most `BatchSize` rows in one database statement.

A snapshot exactly at the cutoff is retained. A full batch means more expired
rows may remain; the next scheduled pass continues from the oldest remaining
row. This gives each pass a fixed database-work bound instead of draining an
unbounded backlog in one run.

The `(CheckedAtUtc, Id)` cleanup index is created concurrently by migration so
snapshot inserts can continue while the index is built.

## Operations

OpenTelemetry instruments:

| Instrument | Meaning |
| --- | --- |
| `goldsrcops.snapshot_retention.runs` | Cleanup runs tagged with `result=success` or `result=failure` |
| `goldsrcops.snapshot_retention.snapshots_deleted` | Number of deleted snapshots |
| `goldsrcops.snapshot_retention.duration` | Run duration in seconds, tagged by result |

The worker logs startup, non-empty successful batches, and failures. Empty
successful passes are represented by metrics without producing an information
log on every interval.

Approximate steady-state cleanup capacity is `BatchSize / CleanupInterval`.
If the expired backlog grows, first shorten the interval or increase the batch
within the validated limits while watching database latency and cleanup
duration.

Deletion is idempotent at the row level, but enabling the worker on every API
replica creates redundant work and lock contention. In a multi-replica v1
deployment, keep `SnapshotRetention:Enabled=true` on one worker process and set
`SnapshotRetention__Enabled=false` on the others.
