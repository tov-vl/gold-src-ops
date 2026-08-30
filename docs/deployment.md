# Container Deployment

This document defines the current container deployment contract. It is
platform-neutral: release tags publish the production image to GitHub Container
Registry (GHCR), and `ops/production` now supplies the provider-independent first
sub-slice of the v2.3 reference Compose contract. The repository does not ship a
provider-specific control-panel integration, systemd unit, or Kubernetes
manifest. GoldSrcOps v2.2.0 is the latest public release. Transactional
incident-alert delivery, audited dead-letter replay, and bounded RCON response
collection extend the application without changing the supported container
shape.

The accepted v2.3 reference-deployment direction is documented in
`docs/v2.3-production-deployment.md`. Immutable image publication is its first
implemented delivery slice. The controlled external game-server contract is in
`docs/v2.3-controlled-gameserver-baseline.md`. A self-operated MyArena game VDS
is the conditional target recorded in
`docs/v2.3-gameserver-provider-decision.md`; purchase, provisioning, and all
target-environment evidence remain pending.

## Supported Shape

The supported baseline consists of:

- the immutable GoldSrcOps API image built from the repository `Dockerfile`;
- an external PostgreSQL database, with PostgreSQL 16 as the verified baseline;
- an external OAuth 2.0 or OpenID Connect provider for bearer tokens;
- an external TLS-terminating reverse proxy or ingress;
- a deployment secret store for the database connection string and RCON
  passwords, plus optional webhook authorization;
- one deployment-configured HTTPS webhook receiver when alert delivery is
  enabled;
- one active polling worker and one active snapshot-retention worker;
- zero or more active alert-dispatch workers, enabled only after the outbox
  migration and receiver are ready;
- a separate, serialized EF Core migration action before application rollout.

The image serves HTTP on container port `8080`, runs as the non-root .NET image
user, and contains only the published ASP.NET Core application. It does not
contain the .NET SDK, `dotnet-ef`, Development configuration, or a built-in
Docker `HEALTHCHECK`.

## Publish And Version The Image

The `Publish Image` CI job runs only after `Quality Gate` and `Container Smoke`
succeed for the tagged revision. Its strict tag contract accepts:

```text
v<major>.<minor>.<patch>
v<major>.<minor>.<patch>-rc.<positive-number>
```

Numeric identifiers do not have leading zeroes, the candidate number starts at
one, and other prerelease or build-metadata forms are rejected. Both stable and
release-candidate tags must be signed annotated Git tags whose revision is
reachable from the default branch. Verify the tag signature before pushing it.

For a previously unpublished revision, the job builds one `linux/amd64` image
under both immutable references:

```text
ghcr.io/tov-vl/gold-src-ops:<exact-stable-or-rc-tag>
ghcr.io/tov-vl/gold-src-ops:sha-<full-git-revision>
```

An RC publication is a deployable candidate, not a stable project release. It
does not publish `latest`, a stable alias, moving major or minor aliases, or a
GitHub Release. If a later stable tag points at the same revision and has the
same base version, the workflow attaches that exact stable tag to the existing
candidate digest instead of rebuilding it. The write-once revision tag and RC
tag remain unchanged. Because labels are part of the immutable image, a promoted
digest retains the RC version label that originally identified its bits; the
workflow summary records both the requested stable tag and promotion mode.

Candidate tags cannot reuse an already published revision. Stable promotion is
allowed only when the revision image has matching source, full revision, MIT
license, and strict RC version labels for the same base version. Existing exact
tags are always rejected, and registry lookup failures fail closed. Registry
write permission is scoped to this job. A separate
`Verify Published Image` job has only package-read permission and runs repository
smoke code against the published digest. OCI labels record the HTTPS source URL,
full Git revision, artifact version, and MIT license. The workflow summaries
record the canonical deployment reference:

```text
ghcr.io/tov-vl/gold-src-ops@sha256:<digest>
```

Deploy only that digest. Tags are discovery metadata, not deployment identity.
Workflow checks prevent accidental replacement through this release path, but a
registry administrator can still mutate or delete package versions. Treat any
such manual operation as an audited exception; publish a new patch version
instead of replacing a released tag.

For a local pre-release build, apply the same minimum labels:

```powershell
$revision = (git rev-parse HEAD).Trim()
$image = "goldsrcops:local-candidate"

docker build --pull `
  --label "org.opencontainers.image.source=https://github.com/tov-vl/gold-src-ops" `
  --label "org.opencontainers.image.revision=$revision" `
  --label "org.opencontainers.image.version=local-candidate" `
  --label "org.opencontainers.image.licenses=MIT" `
  --tag $image `
  .
```

The Dockerfile tracks the servicing `10.0` SDK and ASP.NET runtime image tags,
so rebuilding the same source later can produce a different image. Candidate,
release, and rollback records must therefore retain the published digest rather
than rely on source revision alone. Promoting a matching candidate digest avoids
that rebuild at the stable-release boundary.

Retain at least the digest currently deployed and the immediately preceding
known-good digest. Do not remove either image version while a rollout or rollback
window is open. Longer retention may follow the registry storage policy, but it
must never delete those two protected deployment records automatically.

Build provenance and SBOM attestations are deliberately deferred from this
minimal slice. They need a separately reviewed verification, identity, and
retention policy; merely emitting unsigned or unconsumed metadata would not
create a meaningful supply-chain control. Until that slice is implemented, the
signed Git history, protected CI, OCI labels, registry digest, and digest smoke
test are the evidence boundary.

## Runtime Contract

The production image has these fixed expectations:

| Item | Contract |
| --- | --- |
| Process | `dotnet GoldSrcOps.Api.dll` |
| Container port | `8080` over HTTP |
| Runtime user | Non-root `$APP_UID` from the .NET runtime image |
| Working directory | `/app` |
| Writable path | `/tmp`, plus explicitly mounted state or Unix-socket volumes when the root filesystem is read-only |
| Database migration | Never performed by ordinary application startup |
| Liveness | Anonymous `GET /health/live` |
| Readiness | Anonymous `GET /health/ready`, including PostgreSQL connectivity |
| Metrics | Authenticated `GET /metrics`, requiring `Reader` or `Operator` |

Run the container with a read-only root filesystem, a small `/tmp` tmpfs, no
additional Linux capabilities, and `no-new-privileges`. This shape is exercised
by `tools/smoke/container.ps1`.

The v2.3 reference Compose additionally mounts PostgreSQL's Unix-domain socket at
`/var/run/postgresql`. PostgreSQL runs with `network_mode: none`, so this writable
mount replaces a database TCP boundary rather than exposing one. Its connection
string may set `SSL Mode=Disable` only for that socket path; remote or
TCP-connected PostgreSQL still requires provider-appropriate TLS and certificate
validation.

The following Docker command illustrates the contract for a host-local reverse
proxy. It assumes the named environment variables have already been injected
into the shell by an approved secret/configuration mechanism; do not place
their values in source control or command history.

```powershell
$image = "<registry>/gold-src-ops@sha256:<digest>"

docker run --detach `
  --name goldsrcops `
  --publish 127.0.0.1:8080:8080 `
  --read-only `
  --tmpfs /tmp:rw,noexec,nosuid,size=16m `
  --cap-drop ALL `
  --security-opt no-new-privileges `
  --env ASPNETCORE_ENVIRONMENT `
  --env ConnectionStrings__GoldSrcOps `
  --env Authentication__Schemes__Bearer__Authority `
  --env Authentication__Schemes__Bearer__Audience `
  --env AlertDelivery__Enabled `
  --env AlertDelivery__WebhookUrl `
  --env AlertDelivery__Authorization `
  $image
```

Use the platform's native secret injection instead of a plaintext environment
file when possible. Bind the service to a private network and enforce HTTPS at
the public reverse proxy. The image does not terminate TLS, so the proxy must
reject or redirect public plaintext traffic before forwarding requests and its
scheme/host forwarding must be verified in the target environment. Production
bearer tokens must never cross an unencrypted public connection.

The current source pins the OpenTelemetry SDK and instrumentations to `1.18.0` and the direct
Prometheus exporter to `1.18.0-beta.1`. The endpoint remains prerelease but is
kept for v1 compatibility; Architecture Decision 12 documents the safeguards
and the original migration trigger. Architecture Decision 17 accepts OTLP
metrics through a private OpenTelemetry Collector as the production-default
v2.3 path while preserving authenticated `/metrics` during the v2 compatibility
window. That transition is not part of the current container contract until its
implementation and compatibility checks pass.

## Configuration

Set `ASPNETCORE_ENVIRONMENT=Production`. `appsettings.Local.json` is loaded only
in Development and is not included in the image.

Required deployment values:

| Environment variable | Purpose |
| --- | --- |
| `ConnectionStrings__GoldSrcOps` | Npgsql connection string. TCP deployments require provider-appropriate TLS and certificate validation. `SSL Mode=Disable` is limited to the v2.3 reference Unix socket described above. |
| `Authentication__Schemes__Bearer__Authority` | HTTPS metadata authority for the external identity provider. |
| `Authentication__Schemes__Bearer__Audience` | GoldSrcOps API audience accepted from that provider. |
| `ReverseProxy__KnownProxy` | Optional single trusted proxy IP. When set, the API processes one `X-Forwarded-For` and `X-Forwarded-Proto` hop from that address before HTTPS redirection and authentication. |

Instead of `Authority` and `Audience`, a deployment may provide the equivalent
validated issuer and audience settings described in `docs/security.md`.
Startup fails when the database connection string, issuer/authority, or
audience is absent. HTTPS metadata is mandatory outside Development.

RCON passwords use `RconSecrets__<alias>`, where `<alias>` is the alias stored
for a server credential. They must come from the deployment secret store and
must not be stored in PostgreSQL, image layers, tracked settings, or logs. See
`docs/rcon.md` for the complete secret-reference and retry rules.

Alert delivery is disabled by default. Enabling it requires
`AlertDelivery__WebhookUrl` with an absolute HTTPS URL in Production. Optional
`AlertDelivery__Authorization` contains the complete authorization header value
and must come from the deployment secret store. See `docs/alert-delivery.md`
for the full configuration, retry, telemetry, and recovery contract.

The tracked `appsettings.json` supplies worker defaults. Override only values
that are part of an intentional capacity or topology decision:

| Environment variable | Default | Deployment rule |
| --- | ---: | --- |
| `Polling__Enabled` | `true` | `true` on exactly one v1 process; `false` on every HTTP-only replica. |
| `CommandDispatcher__Enabled` | `true` | May be enabled on multiple replicas because PostgreSQL owns command claims and per-server serialization. |
| `Rcon__ResponseDrainMilliseconds` | `100` | Tune only from owned-server timing evidence; accepted range is `10` to `1000`. |
| `Rcon__MaxResponseDatagrams` | `32` | Bounds response chunks accepted for one command; accepted range is `1` to `256`. |
| `Rcon__MaxResponseBytes` | `65536` | Bounds aggregate wire bytes, including connectionless headers; accepted range is `5` to `1048576`. |
| `SnapshotRetention__Enabled` | `true` | `true` on one process; `false` on other replicas to avoid redundant cleanup contention. |
| `SnapshotRetention__RetentionDays` | `30` | Tune with the validated limits and metrics in `docs/snapshot-retention.md`. |
| `AlertDelivery__Enabled` | `false` | Enable after the additive outbox migration and receiver readiness are verified; multiple replicas are supported. |
| `AlertDelivery__MaxConcurrency` | `4` | Per-process concurrency; size total concurrency across every enabled replica. |

During a rolling deployment, do not overlap the old and new active polling
workers. Start new HTTP-only replicas with polling and retention disabled,
replace the designated worker, then verify that exactly one process owns each
singleton responsibility. Alert delivery uses PostgreSQL claims and may overlap
across compatible v2 replicas, but should first be enabled on one instance at
default concurrency while backlog and receiver behavior are observed.

## Apply Migrations

The runtime image contains a framework-dependent EF Core migration bundle but
does not contain the SDK, EF tool, or repository source. Docker builds the
bundle from the same source revision and copies it into the same immutable image
as the API. The API entrypoint never applies migrations during normal startup.

The migration identity needs the required DDL permissions. Prefer a dedicated
migration credential and a less-privileged runtime credential when the database
ownership and grants have been tested for that split. Serialize migration jobs;
do not let every application replica run one.

Before applying a migration:

1. Confirm the source revision matches the image revision label.
2. Create an encrypted off-host backup and verify its restore procedure as
   defined in `docs/postgresql-backup.md`.
3. Review generated SQL and application/schema compatibility.
4. Ensure no other migration job is running.

For the reference Compose deployment, inject the database connection through
the external file-secret boundary, start PostgreSQL, and invoke the one-shot
`migration` service from the same API image digest:

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

The migration container receives no RCON or identity configuration, has no
network interface or restart policy, and reaches PostgreSQL only through the
shared Unix-domain socket. EF Core's provider migration lock serializes
concurrent bundle executions, but the deployment workflow must still model this
as one explicit job and wait for its zero exit code before starting the API.

EF migration history is stored in `public`; application tables use the
`goldsrcops` schema. The current snapshot-retention index is created
concurrently. Let EF execute that migration outside its normal transaction and
do not wrap the whole migration command in an external database transaction.

v2.0 introduced the additive `AddAlertOutboxPersistence` migration. v2.1 adds
`AddOutboxReplayPersistence` and `AlignDeadLetterListIndex` for replay metadata,
append-only audit, and the dead-letter list order. Apply the complete migration
set before the new application starts, with alert delivery disabled. The v2.1
index migrations use normal PostgreSQL index builds and can block writes to
`outbox_messages`; inspect table size and use a low-traffic rollout. A v2.0
application ignores the additive replay schema, while v1.1 ignores the outbox
tables. Application rollback can therefore leave these migrations in place.
Do not down-migrate while queued, dead-letter, or replay-audit records may still
be required.

Run the same action a second time in a staging or disposable environment to
confirm that the migration set is already up to date. The container smoke test
builds the bundle into the production image, applies it to a clean PostgreSQL
database, repeats it, and only then starts the hardened API container.

## Rollout And Probes

Before the first rollout on a new reference VPS, run the read-only
`ops/production/host-preflight.ps1` baseline described in
`ops/production/README.md`. On a fresh Ubuntu 24.04 host, first use the
plan-first `host-bootstrap.sh` prepare/finalize sequence from that runbook and
verify a separate operator SSH session before finalization. The preflight then
verifies Linux/systemd prerequisites, Docker startup, clock synchronization,
disk and inode headroom, the reviewed UFW and effective key-only SSH policies,
listeners, and published container ports. Repeat it with external endpoint and
runtime-listener checks after DNS, OIDC, backup storage, and Caddy are
configured. Snapshot-mode CI output is not target evidence.

Use this order for a normal release:

1. Build, test, publish, and record the immutable image digest.
2. Back up PostgreSQL and run the single migration action.
3. Start one instance pinned to the digest with singleton workers configured
   for the intended topology.
4. Wait for liveness and readiness before routing traffic.
5. Verify authentication, logs, and the authenticated metrics scrape.
6. Configure the HTTPS webhook and its optional authorization secret, then
   enable alert delivery on one instance and verify backlog telemetry.
7. Replace remaining HTTP-only replicas, keeping polling and retention disabled
   on them. Enable additional alert dispatchers only when receiver capacity
   requires them.

Configure platform probes externally because the image does not bundle an HTTP
client:

| Probe | Endpoint | Meaning |
| --- | --- | --- |
| Startup | `/health/live` | The ASP.NET Core process is accepting requests. |
| Liveness | `/health/live` | The process is alive; it does not test PostgreSQL. |
| Readiness | `/health/ready` | The instance may receive traffic and PostgreSQL is reachable. |

A readiness failure should remove the instance from traffic without
immediately treating the process as dead. Do not use `/metrics` as a probe; it
requires a bearer token by design.

## Rollback

Application rollback and database rollback are separate decisions.

1. Stop the rollout and retain the failing image digest, logs, and deployment
   metadata for diagnosis.
2. Smoke-test the recorded previous known-good digest against the current
   migration set. This pulls the registry artifact and does not rebuild old
   source:

   ```powershell
   $rollbackImage = "ghcr.io/tov-vl/gold-src-ops@sha256:<previous-digest>"
   pwsh -NoProfile -File .\tools\smoke\container.ps1 `
     -ImageReference $rollbackImage
   ```

3. If the previous application is compatible with the migrated schema, redeploy
   that exact digest.
4. Preserve the singleton polling and retention topology during rollback.
5. Verify liveness, readiness, authentication, and metrics before restoring
   traffic.
6. Inspect persisted `Running` or recently failed RCON commands before any
   manual retry; an interrupted remote command can have an unknown outcome.
7. Preserve pending and dead-letter outbox rows. Disabling alert delivery stops
   claims without deleting messages, and a compatible v2 deployment resumes
   from persisted state.

Do not automatically run `dotnet ef database update <old-migration>` during an
application rollback. A down migration can remove data or conflict with writes
made by the new version. When the old application is not schema-compatible,
prefer a forward fix. Restore the pre-migration backup only through an explicit
database recovery procedure with the required write downtime and data-loss
assessment.

## Verification

Before publishing a candidate image, run:

```powershell
pwsh -NoProfile -File .\tools\smoke\container.ps1
```

The protected `main` workflow also requires both `Quality Gate` and
`Container Smoke`. The latter validates plan-only host-bootstrap behavior and
deterministic host-preflight decisions for service startup, time, capacity,
SSH, firewall, port exposure, and external dependency failures. The image smoke
flow also verifies Production webhook HTTPS validation, enabled alert-dispatch
startup, log safety, an encrypted PostgreSQL backup, a full repository data
check, and an isolated restore through the same image-contained migration
bundle. On a release tag,
`Verify Published Image` then pulls the newly published artifact by digest and
reruns the same smoke flow with exact OCI-label expectations. A production
deployment still needs
target-environment evidence for TLS, identity-provider metadata, database TLS,
secret injection, webhook reachability, probe routing, and backup restoration;
the repository smoke test cannot prove those external integrations.
