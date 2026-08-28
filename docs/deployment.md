# Container Deployment

This document defines the current container deployment contract. It is
platform-neutral: the repository does not yet publish an image automatically or
ship provider-specific Docker Compose, systemd, or Kubernetes production
manifests. GoldSrcOps v2.2.0 is the latest public release. Transactional
incident-alert delivery, audited dead-letter replay, and bounded RCON response
collection extend the application without changing the supported container
shape.

The accepted v2.3 reference-deployment direction is documented in
`docs/v2.3-production-deployment.md`. It remains planned work: this document
continues to describe the currently implemented and verified container contract
until each v2.3 slice is integrated with target-environment evidence.

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

## Build And Version The Image

Build from a reviewed, signed commit or release tag. Use an immutable release or
commit tag and deploy the registry digest; do not use `latest` as a deployment
reference.

```powershell
$revision = (git rev-parse HEAD).Trim()
$image = "<registry>/gold-src-ops:<version>"

docker build --pull `
  --label "org.opencontainers.image.revision=$revision" `
  --tag $image `
  .

docker push $image
docker inspect --format '{{index .RepoDigests 0}}' $image
```

Record the resulting `<registry>/gold-src-ops@sha256:<digest>` with the
deployment. The Dockerfile currently tracks the servicing `10.0` SDK and
ASP.NET runtime image tags, so rebuilding the same source later can produce a
different image. Rollback must reuse a previously published digest rather than
rebuild an old tag.

The current CI verifies the image but does not publish it. Registry login,
provenance, signing, and retention policy remain responsibilities of the
release pipeline that publishes a release.

## Runtime Contract

The production image has these fixed expectations:

| Item | Contract |
| --- | --- |
| Process | `dotnet GoldSrcOps.Api.dll` |
| Container port | `8080` over HTTP |
| Runtime user | Non-root `$APP_UID` from the .NET runtime image |
| Working directory | `/app` |
| Writable path | `/tmp` only when the root filesystem is read-only |
| Database migration | Never performed by ordinary application startup |
| Liveness | Anonymous `GET /health/live` |
| Readiness | Anonymous `GET /health/ready`, including PostgreSQL connectivity |
| Metrics | Authenticated `GET /metrics`, requiring `Reader` or `Operator` |

Run the container with a read-only root filesystem, a small `/tmp` tmpfs, no
additional Linux capabilities, and `no-new-privileges`. This shape is exercised
by `tools/smoke/container.ps1`.

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
| `ConnectionStrings__GoldSrcOps` | Npgsql connection string. Production TLS and certificate validation must follow the database provider's requirements; do not copy the smoke test's `SSL Mode=Disable`. |
| `Authentication__Schemes__Bearer__Authority` | HTTPS metadata authority for the external identity provider. |
| `Authentication__Schemes__Bearer__Audience` | GoldSrcOps API audience accepted from that provider. |

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

The runtime image intentionally cannot run EF tooling. Execute migrations once
from the same signed source revision used to build the image, using the pinned
SDK and repository-local tool manifest.

The migration identity needs the required DDL permissions. Prefer a dedicated
migration credential and a less-privileged runtime credential when the database
ownership and grants have been tested for that split. Serialize migration jobs;
do not let every application replica run one.

Before applying a migration:

1. Confirm the source revision matches the image revision label.
2. Take a provider-appropriate backup and verify its restore procedure.
3. Review generated SQL and application/schema compatibility.
4. Ensure no other migration job is running.

Inject the Production connection and bearer configuration through the CI/CD
secret store. Disable all workers in the migration process as a defensive
boundary, then run:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:Polling__Enabled = "false"
$env:CommandDispatcher__Enabled = "false"
$env:SnapshotRetention__Enabled = "false"
$env:AlertDelivery__Enabled = "false"

dotnet restore GoldSrcOps.sln -p:AuditPipeline=true
dotnet tool restore
dotnet tool run dotnet-ef -- database update `
  --project .\src\GoldSrcOps.Infrastructure `
  --startup-project .\src\GoldSrcOps.Api `
  -- `
  --environment Production
```

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

Run the same command a second time in a staging or disposable environment to
confirm that the migration set is already up to date. The container smoke test
performs the real PostgreSQL migration path before starting the API.

## Rollout And Probes

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
2. If the previous application is compatible with the migrated schema, redeploy
   its previously published digest. Do not rebuild the old source.
3. Preserve the singleton polling and retention topology during rollback.
4. Verify liveness, readiness, authentication, and metrics before restoring
   traffic.
5. Inspect persisted `Running` or recently failed RCON commands before any
   manual retry; an interrupted remote command can have an unknown outcome.
6. Preserve pending and dead-letter outbox rows. Disabling alert delivery stops
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
`Container Smoke`. The smoke flow also verifies Production webhook HTTPS
validation, enabled alert-dispatch startup, and that endpoint and authorization
values are absent from application logs. A production deployment still needs
target-environment evidence for TLS, identity-provider metadata, database TLS,
secret injection, webhook reachability, probe routing, and backup restoration;
the repository smoke test cannot prove those external integrations.
