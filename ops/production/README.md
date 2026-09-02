# Reference Production Compose

## Status

This directory contains the provider-independent v2.3 single-node control-plane
contract. It defines and validates the intended container, network, TLS-proxy,
observability, and secret boundaries. It is not target-environment evidence and
does not make the v2.3 milestone complete.

The `runtime` profile must not be enabled on a target until preflight succeeds,
a restorable backup exists, and the one-shot migration action has successfully
migrated that database. Backup and restore automation is implemented, but its
private off-host backend is now initialized and independently escrowed. The
control-plane VPS has passed its two-phase SSH hardening, controlled reboot, and
live baseline host audit. Public DNS and real certificate issuance have also
passed, with certificate data retained in the Compose-labelled Caddy volume.
The external Auth0 issuer, audience, role claim, and dedicated Reader and
Operator logins are configured and verified through the public API. The first
encrypted PostgreSQL backup, full repository check, isolated restore rehearsal,
all eight migrations, idempotent migration rerun, public HTTPS health, complete
eight-scenario authorization matrix, and live runtime host audit have passed.
The signed `v2.3.0-rc.5` image is deployed by its verified digest after a
successful preflight and already-up-to-date migration-bundle run. A guarded
daily backup schedule, scoped retention policy, and freshness probe are active
on the target; the mandatory preview and first completed cycle passed.
Core game-server integration has passed through the public API, including
registered polling, one audited guarded RCON `say`, controlled configuration
restore, post-restart RCON verification, and a 30-minute no-bot A2S check. The
private OTLP Collector, Prometheus, and Grafana services are now deployed on the
target. Their private network, health, live metric path, provisioned Grafana
assets, zero restart counts, and owner-only evidence have passed. Grafana
analytics and both update-check paths are disabled. The controlled recovery
exercise has passed with one incident lifecycle, durable pending work across an
API restart, two successful idempotent-keyed webhook requests, and a verified
rc.4 rollback plus rc.5 roll-forward. The temporary restricted receiver was
removed afterward and alert delivery returned to its disabled baseline. The
accepted 24-hour release soak is active from `2026-09-02T16:23:57Z` through
`2026-09-03T16:23:57Z`. Repository-only work may proceed in a separate branch,
but any production deployment, migration, configuration change, or process
restart invalidates the interval and requires a new baseline.

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
- The API exports metrics over private OTLP gRPC to the OpenTelemetry Collector.
  Prometheus scrapes only Collector endpoints, and Grafana queries Prometheus.
  All three services attach only to an internal telemetry network and publish no
  host ports.
- Collector, Prometheus, and Grafana run as non-root users with read-only root
  filesystems, dropped capabilities, `no-new-privileges`, bounded logs, and
  immutable digest-pinned images. Prometheus and Grafana use named data volumes.
- Container logs use bounded local rotation. Persistent service state uses named
  volumes.

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
| `GOLDSRCOPS_GRAFANA_ADMIN_PASSWORD_FILE` | Unique Grafana administrator password used only for private operator access. |
| `GOLDSRCOPS_RESTIC_PASSWORD_FILE` | Independent password for client-side backup encryption. |
| `GOLDSRCOPS_RESTIC_ENVIRONMENT_FILE` | Repository-scoped S3-compatible backend credentials. |

Docker Compose implements file-backed secrets as bind mounts and cannot remap
their ownership. On Linux, create the PostgreSQL password file with owner UID
`0` and mode `0400`; create the database connection and RCON files with the API
runtime UID `1654` and mode `0400`; create the Grafana password file with UID
`472` and mode `0400`. The PostgreSQL password in the first two files must be the
same. Keep all values out of shell history, source control, image layers, logs,
and issue or pull-request text.

The host also needs:

- DNS for `GOLDSRCOPS_HOSTNAME` pointing to the control-plane host;
- inbound TCP `80`, TCP `443`, and UDP `443` for Caddy;
- outbound HTTPS for ACME and identity-provider metadata;
- no public PostgreSQL, OTLP, Collector telemetry, Prometheus, or Grafana port;
- an external OAuth 2.0 or OpenID Connect issuer that emits the documented
  Reader and Operator roles in the exact claim named by
  `GOLDSRCOPS_AUTHENTICATION_ROLE_CLAIM_TYPE`.

## Host Bootstrap And Hardening

`host-bootstrap.sh` prepares the reference host before any production secret is
copied to it. It supports only a fresh Ubuntu 24.04 x86-64 VPS using systemd.
Without `--apply`, it validates its arguments and prints a sanitized plan.

The script owns the reference host firewall and resets UFW during `prepare`.
Do not run it on a shared or previously configured host. The resulting inbound
policy allows SSH only from the supplied operator IPv4 `/32`, plus public TCP
`80`, TCP `443`, and UDP `443` for Caddy. Docker Engine, Docker Compose,
PowerShell, unattended security updates, UTC/NTP, kernel settings, bounded
required directories, and the non-root `gsoadmin` account are also prepared.

Copy the reviewed script and a dedicated Ed25519 public key to the fresh host.
Run the plan first from the provider-created SSH session, then repeat it with
`--apply` only after checking the CIDR and key path. Preserve `SSH_CONNECTION`
through `sudo`; the apply guard compares its source address and server port with
the requested values before making changes.

```bash
bash /tmp/host-bootstrap.sh \
  --phase prepare \
  --admin-ipv4-cidr 192.0.2.10/32 \
  --operator-public-key-file /tmp/gsoadmin.pub

sudo --preserve-env=SSH_CONNECTION bash /tmp/host-bootstrap.sh \
  --phase prepare \
  --admin-ipv4-cidr 192.0.2.10/32 \
  --operator-public-key-file /tmp/gsoadmin.pub \
  --apply
```

`prepare` intentionally leaves the provider-created login available. Keep that
session open and establish a separate SSH session as `gsoadmin`. From the new
session, review and apply `finalize`. A non-default `--ssh-port` is supported
only when the provider-created session already uses that port; the script does
not migrate SSH between ports.

```bash
sudo --preserve-env=SSH_CONNECTION bash /tmp/host-bootstrap.sh \
  --phase finalize \
  --admin-ipv4-cidr 192.0.2.10/32

sudo --preserve-env=SSH_CONNECTION bash /tmp/host-bootstrap.sh \
  --phase finalize \
  --admin-ipv4-cidr 192.0.2.10/32 \
  --apply
```

The final phase accepts only `sudo` from the prepared operator over the expected
SSH source. It validates the effective OpenSSH configuration before reloading
the service, then disables direct root login and interactive authentication,
requires public keys, and limits login to the operator account. Keep the active
session open until a second post-finalize key-only login succeeds. If
`REBOOT_REQUIRED: yes` was reported, reboot only after that test and verify the
operator login again.

The operator receives passwordless `sudo` because remote access is key-only and
the account is the host administration boundary. It is deliberately not a
member of the `docker` group, whose socket grants root-equivalent control; use
`sudo docker ...` for host operations.

## Host Readiness

The reference host baseline is an x86-64 Linux system using systemd, Docker
Engine with the Compose plugin, and UFW. UFW must deny inbound traffic by
default, allow outbound traffic, restrict SSH to one operator IPv4 `/32`, and
allow only TCP `80`, TCP `443`, and UDP `443` for public Caddy ingress. Choosing
this inspectable host baseline does not couple the application to a VPS vendor.

Run the read-only baseline audit as root before installing production secrets or
starting the runtime profile:

```powershell
sudo pwsh -NoProfile -File ./ops/production/host-preflight.ps1 `
  -AdminIpv4Cidr 192.0.2.10/32 `
  -OperatorUser gsoadmin `
  -EvidenceFile /var/lib/goldsrcops/evidence/host-baseline.json
```

The audit requires an active and boot-enabled `docker.service`, synchronized
time, at least 10 GiB and 10 percent free inodes in Docker's storage filesystem,
the expected UFW policy, effective key-only SSH hardening for the operator, an
SSH listener, and no public PostgreSQL, Docker API, application, OTLP,
Prometheus, or Grafana listener. It also rejects Docker-published ports outside
the Caddy set. Thresholds, the operator name, and the SSH port are explicit
parameters when the reviewed host design requires different values.

After DNS, backup storage, and external identity are configured, repeat the
audit with dependency and runtime checks:

```powershell
sudo pwsh -NoProfile -File ./ops/production/host-preflight.ps1 `
  -AdminIpv4Cidr 192.0.2.10/32 `
  -OperatorUser gsoadmin `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -RequireExternalEndpoints `
  -RequireRuntimeListeners `
  -EvidenceFile /var/lib/goldsrcops/evidence/host-runtime.json
```

This additionally verifies HTTPS reachability of GHCR and the selected backup
endpoint, successful retrieval of OIDC metadata, and the complete Caddy
listener set. Evidence contains versions, capacity, port identifiers, and
pass/fail results, but no host address, administrator CIDR, endpoint, or secret
value. It must remain outside the repository and is atomically published with
mode `0600` on Linux. The script changes no firewall, service, package, or
Docker state.

`tools/smoke/host-bootstrap.sh` checks plan-only behavior, input rejection, and
shell syntax. `tools/smoke/host-preflight.ps1` exercises deterministic passing
and failing snapshots in CI. Such evidence always has `Source: Snapshot` and
`TargetEvidence: false`; it validates the decision logic but cannot close the
live host gate.

## Preflight

Run the preflight before starting target services:

```powershell
pwsh -NoProfile -File ./ops/production/preflight.ps1 `
  -EnvironmentFile /etc/goldsrcops/deployment.env
```

It renders Compose, requires immutable image digests, enforces runtime restart
policies and bounded local logs, checks the public-port and Unix-socket
boundaries, verifies the trusted proxy address, and checks secret file location
and permissions without printing secret contents. It also uses the configured
digest-pinned Caddy image to validate the tracked Caddyfile. Full deployment
mode pulls the API image, verifies its non-root UID, and requires the
image-contained secret-loading entrypoint and migration bundle. It also pulls
the digest-pinned restic image and validates the off-host HTTPS repository and
backup secret boundaries.

CI validates the tracked template with `-ContractOnly`. That mode deliberately
accepts placeholder digests and does not prove target-host files, DNS, firewall,
TLS issuance, identity metadata, migrations, or persistence.

## Backup And Restore

`postgres-backup.ps1` initializes, creates, checks, retains, and schedules
client-side encrypted off-host backups. `postgres-backup-status.ps1` validates
owner-only retention-preview and completed-cycle evidence without opening the
repository. `postgres-restore-rehearsal.ps1` restores a recoverable snapshot
into disposable network-isolated PostgreSQL, runs the migration bundle from the
configured API image, verifies required tables and migration history, and
removes all decrypted volumes.

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

Before enabling the timer, run and review a non-destructive retention preview:

```powershell
sudo pwsh -NoProfile -File ./ops/production/postgres-backup.ps1 `
  -Action Retain `
  -EnvironmentFile /etc/goldsrcops/deployment.env `
  -StatusFile /var/lib/goldsrcops/evidence/postgres-backup-retention-preview.json
```

Then inspect and apply the guarded installer:

```bash
bash ./ops/production/install-postgres-backup-schedule.sh
sudo bash ./ops/production/install-postgres-backup-schedule.sh --apply
```

The installer requires a fresh matching preview before it enables the daily
systemd timer. The timer runs one serialized backup, `5%` data check, scoped
retention/prune, final structural check, and atomic freshness-marker update.

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

## Observability

The production runtime enables the validated OTLP gRPC exporter and sends to the
Collector on the internal `telemetry` network. The API does not depend on the
Collector for startup or readiness. Prometheus scrapes only the Collector's
application-export and self-telemetry endpoints; it does not scrape the
authenticated API `/metrics` compatibility endpoint.

The complete data path, pinned components, private Grafana access, rollout
checks, and failure diagnostics are documented in
[`docs/observability.md`](../../docs/observability.md).

## Target Status

1. Completed: the first encrypted PostgreSQL backup, repeated full data check,
   isolated restore rehearsal, and sanitized target evidence are complete. The
   guarded recurring schedule passed its live retention preview, is enabled as a
   persistent systemd timer, and produced a fresh owner-only cycle marker.
2. Completed: the tracked Caddy route passes HTTP-to-HTTPS redirection,
   certificate validation, anonymous liveness, and PostgreSQL-backed readiness
   through the public hostname.
3. Completed: anonymous requests are rejected; the dedicated Operator token
   passes Reader-policy, Operator-policy, and authenticated metrics checks; and
   the guarded matrix passes with a dedicated Reader-only token plus signed
   missing-role, expired-token, wrong-issuer, and wrong-audience cases. The
   matrix is implemented by `tools/smoke/oidc-live.ps1`; its token provenance,
   expected statuses, and optional sanitized evidence contract are documented
   in `docs/smoke-test.md`. The expired-token input was beyond the API's explicit
   30-second clock-skew allowance.
4. Completed: the runtime host audit retained sanitized listener,
   restart-policy, bounded-log, and external-dependency evidence outside the
   repository. All runtime containers had zero restarts at capture time.
5. Completed: the Collector, Prometheus, and Grafana services are deployed on
   the target. Live application and ASP.NET Core metric queries, the provisioned
   datasource and dashboard, internal-only telemetry network, absent host port
   publication, zero restart counts, and sanitized root-only evidence all
   passed on 2026-09-02. The final Grafana-only recreation disabled analytics
   and both update-check paths without interrupting the other runtime services.

The complete sequence and definition of done remain in
[`docs/v2.3-production-deployment.md`](../../docs/v2.3-production-deployment.md).
