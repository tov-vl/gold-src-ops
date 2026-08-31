# PostgreSQL Backup And Restore

## Scope

The v2.3 single-node baseline uses a PostgreSQL custom-format logical dump
stored in an encrypted restic repository outside the control-plane host. The
repository implementation supports an S3-compatible HTTPS endpoint for
production. A local repository is available only through an explicit test
switch used by the container smoke flow.

The workflow provides:

- a direct `pg_dump` to restic stdin stream, with no plaintext dump on the host;
- separate validation of the `pg_dump` and restic exit codes;
- a structural repository check before a snapshot receives the
  `goldsrcops-postgresql-recoverable` tag;
- configurable repository data checks;
- an isolated restore rehearsal with no network interface;
- migration of the restored database by the exact configured API image; and
- sanitized JSON evidence written only to an operator-selected path outside the
  repository.

This is a logical-backup baseline. It does not provide point-in-time recovery,
continuous WAL archiving, high availability, or zero-data-loss recovery.

## Trust Boundary

Configure these values in the deployment environment file:

| Setting | Requirement |
| --- | --- |
| `GOLDSRCOPS_RESTIC_IMAGE` | Reviewed restic image pinned by SHA-256 digest. |
| `GOLDSRCOPS_BACKUP_HOST` | Stable identifier for this control-plane database. |
| `GOLDSRCOPS_BACKUP_REPOSITORY` | `s3:https://...` endpoint with a bucket and repository path. |
| `GOLDSRCOPS_RESTIC_PASSWORD_FILE` | Independent restic repository password file. |
| `GOLDSRCOPS_RESTIC_ENVIRONMENT_FILE` | Backend credentials in Docker `NAME=VALUE` format. |

The restic password and backend environment files must be non-empty, owned by
the deployment operator, mode `0400` on Linux, and stored outside the
repository. The backend file must not set any `RESTIC_PASSWORD*` or
`RESTIC_REPOSITORY*` variable. For the S3 baseline it contains a repository-
scoped `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY`; add a region or provider
option only when the selected backend requires it.

On Linux, an interactive backup runs the restic container with the caller's
numeric UID and GID. The systemd schedule runs as root because access to the
Docker socket is already root-equivalent; its restic container still has all
Linux capabilities removed, a read-only root filesystem, bounded tmpfs, and
only the required owner-only secret mounts. The installer therefore accepts
only root-controlled source paths and deployment metadata.

Keep the restic password in an independent recovery escrow. Losing it makes the
backup data unrecoverable. Root and Docker-daemon administrators can access
mounted secret files and container environment values, so restrict both roles
and scope the object-storage credential to the backup repository.

## Initialize

Create the remote bucket and repository-scoped credential first. Initialize the
restic repository exactly once:

```powershell
pwsh -NoProfile -File ./ops/production/postgres-backup.ps1 `
  -Action Initialize `
  -EnvironmentFile /etc/goldsrcops/deployment.env
```

Do not reinitialize an existing path. A later `Create` action first verifies
that the configured repository is already readable.

### Reference Target Bootstrap

On 2026-08-30, the reference backend was initialized in a private Backblaze B2
bucket in EU Central. B2 server-side encryption is enabled and the application
key is restricted to that bucket. Object Lock is disabled for this repository
path because the scheduled retention policy requires snapshot and pack deletion.
An append-only or immutable design would require a separate maintenance trust
boundary and remains outside this baseline.

The restic repository was initialized and an integrity check configured with
`-ReadDataSubset 100%` passed using image
`restic/restic@sha256:136600b6ff6843d61d355f7f71f460a166429f35de6fd11b568fece3c9a4d510`.
Backend credentials, the owner-only local recovery file, the independent
encrypted password-manager escrow, and sanitized evidence are all kept outside
Git. Repository, bucket, account, and credential identifiers are deliberately
omitted from tracked documentation.

That bootstrap proved remote repository access and configuration only. The first
production PostgreSQL backup, repeated full data check, and isolated restore
rehearsal passed later as separate gates. They do not replace target timer
activation, its first completed scheduled cycle, or measured recovery-duration
evidence.

## Create A Backup

PostgreSQL must be running through the reference Compose deployment. Create a
backup and store sanitized execution evidence outside the source checkout:

```powershell
pwsh -NoProfile -File ./ops/production/postgres-backup.ps1 `
  -Action Create `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -EvidenceFile /var/lib/goldsrcops/evidence/postgres-backup.json
```

The action streams `pg_dump --format=custom` from the production PostgreSQL
container into restic. A new snapshot starts with a pending tag. It becomes
recoverable only after both processes return zero, a full snapshot identifier
is recorded, and `restic check` validates repository structure. Failed or
partial snapshots are never selected by the restore rehearsal.

Run a backup immediately before every schema migration and on an operator-owned
schedule. The initial reference target should run at least daily, which gives a
maximum schedule-derived recovery point of approximately 24 hours until real
operating evidence supports a different objective.

## Check The Repository

Read and authenticate a sample of stored data on every daily schedule:

```powershell
pwsh -NoProfile -File ./ops/production/postgres-backup.ps1 `
  -Action Check `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -ReadDataSubset 5%
```

Run `-ReadDataSubset 100%` before the first production rollout, after backend or
credential changes, and periodically at a cadence that fits repository size and
download cost. A metadata-only successful backup does not replace data checks
or restore rehearsals.

## Preview Retention And Install The Schedule

The reference retention policy keeps snapshots selected by any of these rules:

- the latest 3 snapshots;
- 14 daily snapshots;
- 8 weekly snapshots; and
- 12 monthly snapshots.

Restic evaluates calendar rules on natural UTC periods that contain snapshots.
When too few periods exist, it may additionally retain the oldest snapshot as a
safe anchor. The policy is restricted to the exact
`GOLDSRCOPS_BACKUP_HOST`, path, and
`goldsrcops-postgresql-recoverable` tag; pending or unrelated snapshots are not
eligible for expiration.

The object-storage credential must permit list, read, write, and delete within
only the selected backup bucket before retention is activated. First run the
non-destructive preview and retain its owner-only evidence:

```powershell
sudo pwsh -NoProfile -File ./ops/production/postgres-backup.ps1 `
  -Action Retain `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -StatusFile /var/lib/goldsrcops/evidence/postgres-backup-retention-preview.json

sudo pwsh -NoProfile -File ./ops/production/postgres-backup-status.ps1 `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -StatusFile /var/lib/goldsrcops/evidence/postgres-backup-retention-preview.json `
  -Kind RetentionPreview `
  -MaximumAgeHours 24
```

Review every snapshot listed for removal. Then inspect the installer plan and
apply it from the root-owned `/opt/goldsrcops` checkout:

```bash
bash ./ops/production/install-postgres-backup-schedule.sh
sudo bash ./ops/production/install-postgres-backup-schedule.sh --apply
```

Apply refuses a source tree outside `/opt/goldsrcops`, a stale or mismatched
preview, production source paths or `deployment.env` that are not root-owned or
are group/other writable, an inactive Docker service, or an invalid unit. It
installs and enables a persistent systemd timer for `03:15 UTC` with a stable
host-specific delay of up to 30 minutes.

Each timer activation holds the shared PostgreSQL recovery lock while it:

1. creates and validates a recoverable backup;
2. reads and authenticates a `5%` repository data sample;
3. applies the scoped retention policy with `forget --prune`;
4. checks repository structure after pruning; and
5. atomically publishes an owner-only success marker.

The marker is not updated after a partial or failed cycle. The service validates
it immediately, while this command provides a monitoring-ready freshness probe
with a 36-hour threshold:

```powershell
sudo pwsh -NoProfile -File ./ops/production/postgres-backup-status.ps1 `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -StatusFile /var/lib/goldsrcops/evidence/postgres-backup-cycle.json `
  -Kind ScheduledCycle `
  -MaximumAgeHours 36
```

Use `systemctl list-timers goldsrcops-postgres-backup.timer` and
`journalctl -u goldsrcops-postgres-backup.service` for schedule and failure
diagnostics. Pause the timer before repository maintenance or an incident where
the oldest known-good snapshots must be frozen:

```bash
sudo systemctl disable --now goldsrcops-postgres-backup.timer
```

Re-run a retention preview before enabling it again. Pruning can hold the restic
repository lock for a long time, so the one-shot service has a four-hour timeout
and must not overlap manual backup or restore actions.

## Rehearse A Restore

Restore the latest recoverable snapshot into a disposable PostgreSQL container:

```powershell
pwsh -NoProfile -File ./ops/production/postgres-restore-rehearsal.ps1 `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -EvidenceFile /var/lib/goldsrcops/evidence/postgres-restore.json
```

Pass `-SnapshotId <full-or-unique-prefix>` to rehearse a specific recoverable
snapshot. The rehearsal:

1. creates temporary PostgreSQL data and socket volumes;
2. streams the encrypted snapshot through `restic dump` into `pg_restore`;
3. runs the migration bundle from `GOLDSRCOPS_IMAGE` against the restored
   database;
4. verifies EF migration history and all required GoldSrcOps tables; and
5. removes the temporary containers and decrypted volumes in `finally`.

Use `-ExpectedMinimumServerCount` when the target has a known lower bound that
helps detect an unexpectedly old snapshot. The rehearsal does not modify the
production database and never starts the API.

## Serialization And Evidence

Backup initialization, creation, checks, retention, scheduled cycles, and restore
rehearsal use one exclusive host lock at
`/var/lock/goldsrcops-postgres-recovery.lock`. If an operator overrides
`-LockFile`, every action must use the same path. Restic's repository lock
remains an independent second layer for remote concurrency.

The evidence files contain image references, snapshot identifiers and times,
database size, migration count, table names, and restored server count. They do
not contain database, restic, or object-storage credentials. Retain them with
deployment records, not in Git.

A target is recovery-ready only after all of these have succeeded against the
configured off-host repository:

- one backup;
- a `100%` repository data check;
- one restore rehearsal using the intended PostgreSQL and API image digests;
- review of the sanitized evidence; and
- a measured recovery duration recorded as the initial recovery-time baseline.

The scheduled retention policy is intentionally conservative and observable. A
failed prune leaves the previous cycle marker stale and fails the systemd unit;
it never triggers automatic repository unlock or backup restoration.

## Failure Handling

- Treat any non-zero `pg_dump`, restic, `pg_restore`, migration, or validation
  exit as a failed recovery operation.
- Do not start a migration when the pre-migration backup or required repository
  check failed.
- Do not delete the previous known-good snapshot during incident response.
- Investigate stale repository locks before running `restic unlock`; never make
  automatic unlock part of the schedule.
- Prefer a forward application/schema fix. Restoring a production backup is an
  explicit write-downtime and data-loss decision, not an automatic rollback.

References:

- [restic repository preparation](https://restic.readthedocs.io/en/stable/030_preparing_a_new_repo.html)
- [restic backup from stdin](https://restic.readthedocs.io/en/stable/040_backup.html#reading-data-from-stdin)
- [restic repository checks](https://restic.readthedocs.io/en/stable/045_working_with_repos.html#checking-integrity-and-consistency)
- [restic dump](https://restic.readthedocs.io/en/stable/050_restore.html#printing-files-to-stdout)
- [restic snapshot retention and pruning](https://restic.readthedocs.io/en/stable/060_forget.html)
- [systemd timer units](https://www.freedesktop.org/software/systemd/man/latest/systemd.timer.html)
