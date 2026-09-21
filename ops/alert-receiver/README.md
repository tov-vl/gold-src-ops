# AlertReceiver Deployment Contract

This directory defines the fail-closed base deployment for the independent
availability-event receiver. The first reviewed placement co-locates its
isolated runtime and database on `gso-control-01` behind the existing production
Caddy. This package is not evidence that the receiver is deployed or that
production alert delivery is enabled.

## Base Boundary

- `receiver` runs the digest-pinned `GoldSrcOps.AlertReceiver` image as UID
  `1654`, accepts only its database and ingress-authorization secrets, and
  starts in `CatchUp` mode with provider delivery disabled.
- `migration` is a networkless one-shot action from the exact receiver image.
- `postgres` uses the dedicated `goldsrcops_receiver` database and role and is
  reachable only through a shared Unix socket volume.
- The standalone profile includes a dedicated `caddy` as the only service that
  publishes host ports. Its public route forwards only
  `POST /api/v1/availability-events`; other paths return `404`.
- The control-plane overlay omits that second proxy, joins the existing
  `goldsrcops_edge` network through an explicit external-network name, and uses
  the production Caddy route. The receiver still publishes no host port.
- Co-location limits receiver PostgreSQL to 1 CPU / 1 GiB and the receiver to
  0.5 CPU / 512 MiB. The dedicated database, role, volumes, secrets, backup
  namespace, and migration history remain separate from the control plane.
- Every long-running service uses bounded local logs. Application containers
  use read-only roots, dropped capabilities, and `no-new-privileges`.

The base contract deliberately mounts no provider endpoint or provider
authorization secret. `compose.muted-provider.yml` is a separate reviewed trial
overlay. It switches the receiver to `Live`, enables exactly one provider
dispatcher, and mounts both provider values from owner-only files. The overlay
must target the dedicated muted integration with no escalation chain; it is not
a production activation contract.

Provider review access is a second, independent overlay. The receiver-side
`compose.provider-operations.yml` mounts only the dedicated operations
authorization and leaves provider delivery disabled. The production-side file
with the same name enables the server-side Web client against
`http://goldsrcops-alert-receiver:8080/` and mounts the same owner-controlled
file. Neither overlay changes the production Caddy allowlist, so
`/internal/v1/provider-delivery/*` is never a public route.

## Validate

Before deployment, record the approved receiver DNS name, immutable image
digests, and backup namespace outside Git. A local container pass does not
establish any of these target prerequisites. Co-location saves a second VDS but
does not provide an independent failure domain: a control-plane host outage or
maintenance window also removes the receiver. The API/Web release workflow
does not publish this image.

CI validates the tracked placeholder contract:

```powershell
pwsh -NoProfile -File ./ops/alert-receiver/preflight.ps1 `
  -EnvironmentFile ./ops/alert-receiver/deployment.env.example `
  -ContractOnly
```

For a target host, copy `deployment.env.example` outside Git, replace every
placeholder with an immutable image or target-specific value, create the three
owner-only secret files, and run the same command without `-ContractOnly`.
Preflight reads secret metadata but never prints secret values.

The `gso-control-01` placement uses the additional fail-closed cross-stack
preflight. Its receiver environment must name the already existing production
edge network, while both environment files must contain the same distinct
receiver hostname:

```powershell
pwsh -NoProfile -File ./ops/alert-receiver/control-plane-preflight.ps1 `
  -ReceiverEnvironmentFile /etc/goldsrcops-alert-receiver/deployment.env `
  -ProductionEnvironmentFile /etc/goldsrcops/deployment.env
```

The preflight proves that the standalone receiver Caddy is inactive, the
receiver publishes no host ports, both stacks resolve the same external edge
network, and the production Caddy exposes only the reviewed POST path. For a
future dedicated host, omit `compose.control-plane.yml` and activate both the
`runtime` and `standalone` profiles after a fresh placement review.

Provider operations has a separate cross-stack preflight. Both environment
files must reference the same owner-only, single-line Bearer authorization
file. The check proves that the base deployments remain disabled, the reviewed
overlays enable only provider review access, Web uses the private receiver
alias, provider delivery stays off, and Caddy still exposes no internal route:

```powershell
pwsh -NoProfile -File ./ops/alert-receiver/provider-operations-preflight.ps1 `
  -ReceiverEnvironmentFile /etc/goldsrcops-alert-receiver/deployment.env `
  -ProductionEnvironmentFile /etc/goldsrcops/deployment.env
```

Activation requires both receiver Compose files plus
`compose.provider-operations.yml`, and the production Compose file plus its own
same-named overlay. Applying only one side is invalid: the feature remains
unavailable or the Web host fails closed without its file-backed secret.

The muted-provider overlay has its own preflight. `-ContractOnly` validates the
tracked placeholder shape without reading either provider secret:

```powershell
pwsh -NoProfile -File ./ops/alert-receiver/muted-provider-preflight.ps1 `
  -EnvironmentFile ./ops/alert-receiver/deployment.env.example `
  -ContractOnly
```

Without `-ContractOnly`, the preflight also requires both provider files to be
outside Git, owner-only, single-line values. The endpoint must be HTTPS and the
authorization value must be a bounded Bearer credential. Neither value is
printed or retained.

The isolated container smoke builds locally by default. To verify a published
artifact without rebuilding, pass its immutable GHCR reference:

```powershell
pwsh -NoProfile -File ./tools/smoke/alert-receiver-container.ps1 `
  -Image $reviewedReceiverDigestReference
```

This verifies ingestion and duplicate handling through the bounded Caddy route,
recovery after restart with submicrosecond source timestamps, disabled and
authorized provider-operations behavior, public rejection of the internal
route, and encrypted backup/restore plus migration reapplication. It never uses
the production database or provider credentials.

## Migration And Activation Order

1. Create a fresh encrypted `AlertReceiver` workload backup when upgrading an
   existing receiver database and complete a `100%` repository check.
2. Run the `operations` profile migration action from the reviewed image.
3. Create a fresh encrypted receiver backup, including for a first deployment,
   complete a `100%` repository check, and rehearse its isolated restore and
   migration reapplication.
4. On `gso-control-01`, start PostgreSQL and the `runtime` profile with both
   `compose.yml` and `compose.control-plane.yml`, then recreate the production
   Caddy from its reviewed Compose contract. Confirm liveness, readiness, three
   receiver migration-history rows, `CatchUp`, disabled provider delivery and
   provider operations, and that non-POST or non-matching receiver paths return
   `404`.
5. Stop. Enabling a muted provider route, catch-up of historical production
   rows, and any live canary are separate gates.

Rollback stops the receiver and preserves its PostgreSQL data for
reconciliation. Do not run a down migration or delete accepted event or outbox
rows.
