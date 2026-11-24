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
