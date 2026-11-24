# Live PostgreSQL WAL monitoring rehearsal

The rehearsal starts PostgreSQL 16, a collector, Prometheus, Alertmanager
and a webhook observer in an internal Docker network. PostgreSQL stores WAL
on a dedicated test volume. The collector runs as a non-root user with no
Linux capabilities and reads that volume through a read-only mount.
It needs no PostgreSQL login. The archive, database and monitoring
containers use synthetic data and disposable storage.

The collector exposes three gauges on every successful scrape.

| Gauge suffix after goldsrcops_postgres_wal_ | Source |
| --- | --- |
| ready_segments | Regular files named for complete WAL segments with a .ready marker |
| oldest_ready_age_seconds | Age of the oldest ready marker, or zero for an observed empty queue |
| filesystem_available_bytes | Available blocks on the filesystem containing the WAL directory |

The collector has a bounded observation deadline. A missing directory or
read timeout fails the scrape. In the rehearsal, pausing the collector
produces up=0 and removes the three gauges from instant queries. Resuming it
produces up=1 and exposes the observed empty queue.

Pgbench writes throughout an archive outage of at least 150 seconds and its
catch-up. The test compares the collector with PostgreSQL's queue inspection
and the source filesystem's available bytes. It requires a firing and a
resolved notification with the same alert fingerprint on the local webhook.
The optional critical run holds the outage until the oldest ready segment
reaches 240 seconds and verifies the warning-to-critical transition.

The disk candidates correspond to 15 and 5 minutes at a provisional 4 MiB/s
design rate. This rehearsal compares live available bytes and exercises the
age alert. Disk threshold boundaries are controlled cases. The ready-marker
age measures complete segments queued for archiving. The time to close the
active WAL segment also matters when sizing an end-to-end recovery objective.
[PostgreSQL WAL archiving](https://www.postgresql.org/docs/16/continuous-archiving.html)

Run from the repository root with a new output directory:

```sh
python -B tools/smoke/postgres-wal-monitor/rehearse.py --pull --output artifacts/wal-live
```

Add the Docker context argument for a nondefault engine or the critical
switch for the longer test. Full CI runs the normal profile in Quality Gate
and retains the JSON receipt, the collector checks and the writer log.

The first local run observed 14 ready segments at the independent
PostgreSQL check. The age warning fired about 129 seconds after archive
disconnect and the queue drained in about 4 seconds after reconnect.
The writer committed 18,025 rows during the outage and 507 during
catch-up. The webhook recorded the firing and resolved pair and the
separate scrape-timeout check passed.
