# Verify and rehearse a PostgreSQL PITR chain

The read-only checker uses a trusted SHA-256 of a catalog that binds a
physical base tar, its native backup manifest and required WAL through a
named target LSN. It checks sizes, hashes, the physical cluster identifier,
the timeline, each segment's start address and continuity. Use it with an
owner-controlled stage and an independently obtained catalog digest.

This rehearsal covers PostgreSQL 16 on Linux amd64, one timeline and 16 MiB
WAL segments. It runs pg_verifybackup against both extracted synthetic bases.
It then stops the source and verifies exact rows in two fresh restores served
from verified bytes through read-only mounts. The preflight also rejects an
absent interior segment, a corrupted segment and bytes from another actual
cluster under the expected filename with a valid digest for those bytes.

```sh
python -B tools/smoke/postgres-pitr-chain/rehearse_chain.py --pull --output artifacts/pitr-chain
```

Pass --context desktop-linux to select the Docker Desktop engine if needed.
Use a new output directory each time. CI retains the results, the retention
simulation, the synthetic catalogs and restore logs. The rehearsal cleans up
its labeled Docker containers, volumes and network.

A staged archive contains chain.json, backup_manifest, base.tar.gz and the
wal directory. The checker prints a JSON verdict and exits 2 on rejection.

```sh
python -B tools/smoke/postgres-pitr-chain/pitr_chain.py --archive "$STAGED_ARCHIVE" --catalog-sha256 "$TRUSTED_CATALOG_SHA256"
```

[PostgreSQL base backup verification](https://www.postgresql.org/docs/16/app-pgverifybackup.html)
[PostgreSQL manifest WAL range](https://www.postgresql.org/docs/16/backup-manifest-wal-ranges.html)

## Exact object versions

The rehearsal now stages the older base through a disk-backed versioned
archive fixture. Its independently anchored inventory records the exact
version, byte count and SHA-256 of each object. A newer wrong payload under
the same key leaves the requested older version available. Restore uses
only the checked stage.

Wrong bytes, an absent pinned version and a changed inventory digest all
fail admission. A single committed upload with a lost acknowledgment is
reconciled by exact lookup and content verification. Multiple versions
leave the upload unresolved. Full CI retains the synthetic version catalog
with the result.

## One direct timeline fork

The timeline rehearsal uses pg_promote on a paused restore and creates two
child timelines from a shared parent. Its version 2 catalog binds the exact
native history file. It checks the required parent range up to the recorded
fork and the required child range after it. The first child segment can
contain a prefix from its parent. Its digest remains anchored and the test
checks independent PostgreSQL replay across that boundary.

The rehearsal reaches named targets on both sides of the fork with all
producers stopped. It excludes writes from the unselected branches. Missing
history or a substituted sibling WAL segment fails admission. At the fork,
pg_waldump checks the native END_OF_RECOVERY record. A substituted first
segment from the sibling fails this check even when its inherited page
header matches. The child restore stages both history and segment versions through the versioned
archive fixture. Full CI retains the separate timeline result.

```sh
python -B tools/smoke/postgres-pitr-chain/rehearse_timeline.py --pull --output artifacts/pitr-timeline
```

[PostgreSQL timeline behavior](https://www.postgresql.org/docs/16/continuous-archiving.html#BACKUP-TIMELINES)
