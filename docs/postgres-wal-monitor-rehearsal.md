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

## Six-minute SeaweedFS controls

The November 25 controls used PostgreSQL 16 at a target of 80 transactions
per second and SeaweedFS 4.51 with versioning. Both kept writes active while
the archive adapter lost its route for six minutes. In each run pg_wal grew
from 112 to 256 MiB and nine complete segments, 144 MiB, queued.

| Measurement | Same host | Two hosts over Wi-Fi |
| --- | ---: | ---: |
| Route restored to S3 ready | 0.45 s | 1.99 s |
| S3 ready to empty outage backlog | 15.28 s | 15.20 s |
| Route restored to repeated SHA-256 verification | 29.20 s | 30.94 s |

Both independently restored a named target from 15 pinned WAL versions
with the source stopped. The pair gives one observation per route, with
the host topology also changing. The existing 120-second and 240-second
ready-age thresholds continue to describe the queue. The measured transfer
and verification times are inputs for a separate commit-to-recovery check.

## Commit-to-archive rehearsal

A four-hour loopback run on November 25 used a 60-second archive timeout,
15 sparse commits and idle controls, then three hours of sustained writes
with a burst. The runner read the PostgreSQL commit timestamp and a
conservative WAL insert LSN bound after the transaction became visible.
It compared that bound with the contiguous archive frontier covered by
individually verified S3 object versions.

A read-only observer recorded the times of ready markers. PostgreSQL
renames the same marker to done after archive success. For the fifteen
sparse commits, the ready marker appeared 54.75 to 56.93 seconds after
commit. Verification was observed another 6.29 to 10.23 seconds later,
including the sample interval. The final sparse commit was covered
through the subsequent idle period with an empty pending set.
[PostgreSQL ready-to-done rename](https://github.com/postgres/postgres/blob/REL_16_15/src/backend/postmaster/pgarch.c)

| Isolated route interruption | Writes during interruption | Complete segments at reconnect | First observed outcome |
| --- | ---: | ---: | --- |
| 150.72 s during sustained load | 12,112 | 3 | Sampled commits covered within 178.06 s |
| 360.92 s during burst load | 57,660 | 16 | Sampled unverified commit age exceeded five minutes |

In the long interruption the first sample meeting the critical ready-age
condition preceded the first sample with an unverified commit older than
five minutes by 42.59 seconds. In the short interruption the first
warning appeared during catch-up. The trace was replayed against the
existing Prometheus rules. A controlled missing scrape produced up=0 and
absence of the three gauges. An observed empty queue produces up=1 and
zero ready segments.

These measurements are from a synthetic single-host run with a sampled
archive frontier. Keep the queue-age and available-space rules as
operational signals. A production recovery objective also requires
target-specific commit coverage, end-to-end recovery timing and an
explicit interruption envelope.

Three independent recoveries reached their named targets with the source
and its writer-facing adapter stopped. Full time covers download, version
and checksum verification, preparation, PostgreSQL replay and SQL checks.

| Target | Pinned WAL segments | Restored workload rows | Full time | PostgreSQL replay and target checks |
| --- | ---: | ---: | ---: | ---: |
| Before the short interruption | 37 | 47,632 | 97.13 s | 15.63 s |
| Before the long interruption | 133 | 384,063 | 285.86 s | 49.67 s |
| End of observation | 291 | 939,437 | 625.44 s | 122.33 s |

The physical base passed pg_verifybackup for all three targets. The independent audit
reconciled 1,422 source samples, 1,069 sampled transaction witnesses, 15
sparse segment closures and the hashes of the pinned recovery catalogs.

A separate 45-minute sparse control began its 271.25-second interruption
while a sampled commit had already waited 46.24 seconds in an open WAL
segment. The commit was still outside the archive at reconnect, at an
age of 317.62 seconds. Its covering version was observed verified at
359.32 seconds. The first sample showing a critical ready-marker age
also showed an unverified commit older than five minutes. The independent
restore verified 27 pinned segments and all 17 expected rows in 111.91
seconds from preparation through SQL verification.

Use both commit age and the archive frontier when assessing a near-boundary
interruption. The proposed five-minute objective needs enough margin for
the active segment, transfer and independent verification.

## Commit coverage alert prototype

The isolated monitor takes a fresh PostgreSQL observation and the end of
the contiguous range verified in the versioned archive. Each sampled commit
has a conservative WAL upper bound. A commit stays pending while that bound
is ahead of the verified range. The age is measured from the oldest pending
commit timestamp, including the time spent in an open segment. An upload
only advances the frontier after readback of the selected version verifies
its length and SHA-256.

| State | Observation | Alert |
| --- | --- | --- |
| Covered | Fresh observation with no pending sampled commits, including an idle database | None |
| Pending | Fresh observation with sampled commits ahead of the verified archive | Warning from 180 seconds and critical from 240 seconds |
| Unknown | Snapshot missing, malformed or older than 25 seconds, or the configured collector cannot be scraped | Unknown warning. A recent critical remains active during the short gap, while its last age sample remains in the 30-second lookback. |

A fresh pending or covered observation resumes normal evaluation. Read the
Unknown warning as a loss of measurement. The numeric age is absent in that
state. Samples bound the observed workload only. The prototype depends on the
producer sampling the same PostgreSQL timeline and supplying verified version
receipts without gaps. These thresholds are isolated acceptance criteria.
[Prometheus alerting rules](https://prometheus.io/docs/prometheus/latest/configuration/alerting_rules/)

Replay of the two previously retained traces reproduced all 1,723 frames.
On those recorded observations the 240-second condition was reached
63.81 seconds before the first five-minute breach in the burst case and
57.00 seconds before it in the sparse open-segment case. The short cut
never met the critical condition.

The Quality Gate publishes the short rule checks and source hashes under
`commit-coverage/` inside the WAL monitor artifact. For a local replay,
first run the existing WAL monitoring command above to load the pinned
Prometheus image, then run the following command with a fresh output path.

```sh
python -B tools/smoke/postgres-wal-monitor/rehearse_commit_rules.py --output artifacts/wal-commit-rules
```

### Five-hour live rehearsal

The loopback rehearsal used PostgreSQL 16, a 60-second archive timeout,
and a separate versioned SeaweedFS 4.51 receiver process. It took 1,791
source samples. The measured steady and burst rates were 79.82 and 160.24
commits per second.

| Archive adapter route interruption | Queued versions at reconnect | Peak sampled commit age | Critical lead before the first sample above five minutes | Route restored through backlog verification and alert clearance |
| --- | ---: | ---: | ---: | ---: |
| 151.44 s during steady writes | 3 | 155.59 s | No critical alert | 24.48 s |
| 270.94 s with a commit in an open segment | 3 | 318.33 s | 65.08 s | 49.02 s |
| 361.48 s during the burst | 16 | 365.80 s | 60.26 s | 33.92 s |

The commit in the open segment was already 46.74 seconds old at the
start of the sparse interruption. The short and sparse interruptions
left total pg_wal allocation unchanged. It grew by 80 MiB during the
burst interruption.

Removing the input snapshot yielded Unknown with no numeric age. The
next fresh observation restored the known state. During the burst
interruption a 9.74-second restart of the observer raised Unknown beside
the existing critical. Its firing time stayed unchanged, and the
critical cleared after the verified archive advanced. The recorded
Prometheus history contained no gap in the critical state during the
restart.

After the five-hour observation the PostgreSQL source and writer adapter
stopped. An independent reader verified the exact S3 versions and
SHA-256 digests of the physical base, manifest and 325 WAL segments.
PostgreSQL passed pg_verifybackup and reached the named recovery point.
SQL returned both receipt rows and the sampled transaction at the
target, among 1,037,289 restored synthetic rows. The full preparation,
download, verification and restore took 650.33 seconds, of which
136.08 seconds covered native recovery and its target checks.
