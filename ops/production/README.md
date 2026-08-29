# Reference Production Compose

## Status

This directory contains the provider-independent first sub-slice of the v2.3
single-node control plane. It defines and validates the intended container,
network, TLS-proxy, and secret boundaries. It is not target-environment evidence
and does not make the v2.3 milestone complete.

The `runtime` profile must not be enabled on a target until preflight succeeds,
a restorable backup exists, and the one-shot migration action has successfully
migrated that database. Backup and restore automation is implemented, but its
off-host target evidence, external identity, public HTTPS, firewall, and broader
recovery evidence remain pending.

## Topology

- Caddy is the only service that publishes host ports: TCP `80`, TCP `443`, and
  UDP `443`.
- The API is reachable only through the private `edge` network. Its static proxy
  setting trusts forwarded headers from the configured Caddy address only.
- PostgreSQL uses `network_mode: none` and exposes no TCP listener to another
  container or the host. The API reaches it through a shared Unix-domain socket.
- The `operations` profile contains a one-shot migration service. It uses the
  exact API image, receives only the database secret, has no network interface
  or restart policy, and reaches PostgreSQL through the same socket volume.
- API, PostgreSQL, and Caddy images must all be supplied by immutable SHA-256
  digest.
- The API runs with a read-only root filesystem, no Linux capabilities, and
  `no-new-privileges`.
- Container logs use bounded local rotation. Persistent PostgreSQL and Caddy
  state use named volumes.

The database connection secret should use this shape, with an independently
generated password:

```text
Host=/var/run/postgresql;Port=5432;Database=goldsrcops;Username=goldsrcops;Password=<secret>;SSL Mode=Disable;Timeout=5;Command Timeout=30
```

`SSL Mode=Disable` is accepted only for this Unix-domain socket. PostgreSQL has
no network interface in this topology, so database traffic never crosses a TCP
network boundary. Do not reuse this setting for a remote or TCP-connected
database.

## Deployment Inputs

Copy `deployment.env.example` to a root-owned path outside the repository, such
as `/etc/goldsrcops/deployment.env`, and replace every example value. The file
contains deployment metadata and paths, not secret values.

Create these separate secret files outside the repository:

| Setting | Content |
| --- | --- |
| `GOLDSRCOPS_POSTGRES_PASSWORD_FILE` | PostgreSQL role password used during database initialization. |
| `GOLDSRCOPS_DATABASE_CONNECTION_FILE` | Complete single-line Npgsql connection string using the shared Unix socket. |
| `GOLDSRCOPS_RCON_PASSWORD_FILE` | Single-line RCON password for `GOLDSRCOPS_RCON_SECRET_ALIAS`. |
| `GOLDSRCOPS_RESTIC_PASSWORD_FILE` | Independent password for client-side backup encryption. |
| `GOLDSRCOPS_RESTIC_ENVIRONMENT_FILE` | Repository-scoped S3-compatible backend credentials. |

Docker Compose implements file-backed secrets as bind mounts and cannot remap
their ownership. On Linux, create the PostgreSQL password file with owner UID
`0` and mode `0400`; create the database connection and RCON files with the API
runtime UID `1654` and mode `0400`. The PostgreSQL password in the first two
files must be the same. Keep all values out of shell history, source control,
image layers, logs, and issue or pull-request text.

The host also needs:

- DNS for `GOLDSRCOPS_HOSTNAME` pointing to the control-plane host;
- inbound TCP `80`, TCP `443`, and UDP `443` for Caddy;
- outbound HTTPS for ACME and identity-provider metadata;
- no public PostgreSQL port;
- an external OAuth 2.0 or OpenID Connect issuer that emits the documented
  Reader and Operator roles.

## Preflight

Run the preflight before starting target services:

```powershell
pwsh -NoProfile -File ./ops/production/preflight.ps1 `
  -EnvironmentFile /etc/goldsrcops/deployment.env
```

It renders Compose, requires immutable image digests, checks the public-port and
Unix-socket boundaries, verifies the trusted proxy address, and checks secret
file location and permissions without printing secret contents. It also uses the
configured digest-pinned Caddy image to validate the tracked Caddyfile. Full
deployment mode pulls the API image, verifies its non-root UID, and requires the
image-contained secret-loading entrypoint and migration bundle. It also pulls
the digest-pinned restic image and validates the off-host HTTPS repository and
backup secret boundaries.

CI validates the tracked template with `-ContractOnly`. That mode deliberately
accepts placeholder digests and does not prove target-host files, DNS, firewall,
TLS issuance, identity metadata, migrations, or persistence.

## Backup And Restore

`postgres-backup.ps1` initializes, creates, and checks client-side encrypted
off-host backups. `postgres-restore-rehearsal.ps1` restores a recoverable
snapshot into disposable network-isolated PostgreSQL, runs the migration bundle
from the configured API image, verifies required tables and migration history,
and removes all decrypted volumes.

The scripts share one exclusive host lock and write optional sanitized evidence
only to an operator-selected path outside the repository. Configure and verify
the complete workflow before enabling `runtime`:

```powershell
pwsh -NoProfile -File ./ops/production/postgres-backup.ps1 `
  -Action Create `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -EvidenceFile /var/lib/goldsrcops/evidence/postgres-backup.json

pwsh -NoProfile -File ./ops/production/postgres-backup.ps1 `
  -Action Check `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -ReadDataSubset 100%

pwsh -NoProfile -File ./ops/production/postgres-restore-rehearsal.ps1 `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -EvidenceFile /var/lib/goldsrcops/evidence/postgres-restore.json
```

Initialization, secret ownership, cadence, failure handling, and residual
logical-backup limits are documented in
[`docs/postgresql-backup.md`](../../docs/postgresql-backup.md).

## Migration Action

The Docker build creates the framework-dependent `/app/goldsrcops-migrate`
EF Core migration bundle and copies it into the same runtime image as the API.
The API does not run migrations during startup. The Compose `migration` service
invokes the bundle only through the explicit `operations` profile, and EF Core's
provider migration lock serializes concurrent bundle executions.

After preflight and a verified backup, start PostgreSQL and run the one-shot
action from the repository root:

```powershell
docker compose `
  --env-file /etc/goldsrcops/deployment.env `
  --file ./ops/production/compose.yml `
  --profile operations `
  up --detach --wait postgres

docker compose `
  --env-file /etc/goldsrcops/deployment.env `
  --file ./ops/production/compose.yml `
  --profile operations `
  run --rm migration
```

Treat a zero exit code as the migration gate. Do not start the `runtime` profile
after an interrupted or failed action. Repeat the command in a disposable or
staging database to prove the already-up-to-date path; the container smoke test
does this automatically. The bundle has no down-migration mode in this workflow.

## Remaining Slice 3 Work

1. Run encrypted backup, full repository check, and restore rehearsal against
   the selected off-host repository and retain sanitized target evidence.
2. Validate Caddy certificate issuance, forwarded scheme, AllowedHosts, and
   anonymous liveness plus PostgreSQL-backed readiness through public HTTPS.
3. Validate Reader, Operator, expired-token, wrong-issuer, and wrong-audience
   behavior against the selected external identity provider.
4. Record host firewall, time synchronization, disk, service restart, and log
   retention evidence without storing account or secret data.

The complete sequence and definition of done remain in
[`docs/v2.3-production-deployment.md`](../../docs/v2.3-production-deployment.md).
