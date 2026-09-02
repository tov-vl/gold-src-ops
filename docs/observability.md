# Production Observability

## Status

The repository-side v2.3 observability contract and its first target rollout
are complete. On 2026-09-02, signed candidate `v2.3.0-rc.5` was deployed by its
verified image digest. The private Collector, Prometheus, and Grafana services
passed production preflight, health, metric-path, provisioning, and host-network
checks. Sanitized target evidence remains outside Git in the root-only
`/var/lib/goldsrcops/evidence` directory.

## Data Path

The production path is deliberately private:

```text
GoldSrcOps API
  -> OTLP gRPC :4317
OpenTelemetry Collector
  -> Prometheus exporter :9464
Prometheus :9090
  <- Grafana :3000

Prometheus also scrapes Collector internal telemetry on :8888.
Collector health is available on :13133.
```

All observability ports exist only on the internal Docker `telemetry` network.
They are not published on the host and Caddy does not route them. The
API keeps the `edge` network as its egress gateway and uses the telemetry
network only to send OTLP metrics.

The authenticated API `/metrics` endpoint remains available for v2
compatibility, local development, and rollback. Production Prometheus does not
scrape it, so direct and OTLP observations are never added together.

## Components

`ops/production/deployment.env.example` is the source of truth for reviewed
image references. The current contract pins these releases by multi-platform
digest:

- OpenTelemetry Collector Contrib `0.159.0`;
- Prometheus `3.14.0`;
- Grafana `13.2.1`.

The Collector accepts only OTLP gRPC metrics, applies a memory limiter and
batching, and exposes a private Prometheus target. Prometheus retains at most 15
days or 1 GB, whichever limit is reached first. Grafana provisions one immutable
Prometheus datasource and the `GoldSrcOps Operations` dashboard from tracked
files. Prometheus and Grafana data use named volumes; Collector state is
ephemeral.

## Secret And Access Boundary

Create `GOLDSRCOPS_GRAFANA_ADMIN_PASSWORD_FILE` outside the repository with a
unique generated password. On the Linux target, the file must be owned by UID
`472`, use mode `0400`, and contain only the password. Do not place it in the
environment file, shell history, logs, or deployment evidence.

Anonymous access, self-registration, analytics reporting, core update checks,
plugin update checks, and startup plugin downloads are disabled. The reference
deployment does not expose Grafana through Caddy. Open an SSH tunnel only for
the operator session that needs it:

```powershell
ssh -N -L 3000:<GOLDSRCOPS_GRAFANA_IP>:3000 <operator>@<control-plane-host>
```

Then browse to `http://127.0.0.1:3000` and sign in as `admin`. Close the SSH
session when finished. Do not add a temporary Docker port publication or a
public firewall rule.

## Deployment And Verification

Run the full production preflight before changing the runtime:

```powershell
sudo pwsh -NoProfile -File ./ops/production/preflight.ps1 `
  -EnvironmentFile /etc/goldsrcops/deployment.env
```

After the existing backup and migration gates pass, start or update the runtime
from the reviewed source tree:

```powershell
sudo docker compose `
  --env-file /etc/goldsrcops/deployment.env `
  --file ./ops/production/compose.yml `
  --profile runtime `
  up --detach --remove-orphans
```

Check container health and bounded recent logs:

```powershell
sudo docker compose `
  --env-file /etc/goldsrcops/deployment.env `
  --file ./ops/production/compose.yml `
  --profile runtime `
  ps

sudo docker compose `
  --env-file /etc/goldsrcops/deployment.env `
  --file ./ops/production/compose.yml `
  logs --tail 100 otel-collector prometheus grafana
```

After at least two scrape intervals, query Prometheus from inside its container:

```powershell
sudo docker compose `
  --env-file /etc/goldsrcops/deployment.env `
  --file ./ops/production/compose.yml `
  exec -T prometheus wget -qO- `
  'http://127.0.0.1:9090/api/v1/query?query=up%7Bjob%3D%22goldsrcops%22%7D'
```

The result must contain one series with value `1`. In Grafana, confirm that the
provisioned datasource is healthy and that the Operations dashboard has recent
API and GoldSrcOps application data. Finally, rerun host readiness and retain
sanitized evidence that ports `3000`, `4317`, `8888`, `9090`, `9464`, and
`13133` are not publicly listening.

In this topology, `up{job="goldsrcops"}` reports Prometheus reachability of the
Collector's application-export endpoint. It does not probe the public API and
must not be presented as API uptime. Continuous HTTP availability requires a
separate external black-box time series; until then, public health evidence is
explicitly sampled. The inactive objective and its measurement gate are recorded
in [service-level objectives](service-level-objectives.md).

### First Target Result

The first target rollout completed on 2026-09-02 from signed tag
`v2.3.0-rc.5`, revision
`58a74dafecb7eefe3b4f1e310347dbafb94d4b05`, and API image digest
`sha256:f146c61e5eba942fc40d27792088ec6666fb2605959429093930c30a43f7d639`.
Tag workflow
[#204](https://github.com/tov-vl/gold-src-ops/actions/runs/33631016313)
published and verified that image. The final Grafana outbound-update hardening
is recorded in
[pull request #62](https://github.com/tov-vl/gold-src-ops/pull/62) and was
validated by workflow runs
[#205](https://github.com/tov-vl/gold-src-ops/actions/runs/33635546123)
and
[#206](https://github.com/tov-vl/gold-src-ops/actions/runs/33635585889).

The target checks established all of the following:

- public liveness and PostgreSQL-backed readiness returned `200`;
- `up{job="goldsrcops"}` was `1`, and both GoldSrcOps polling and ASP.NET Core
  request metrics were present;
- the provisioned datasource reported `OK`, and dashboard
  `goldsrcops-operations` was provisioned with `editable=false`;
- the telemetry network was internal and non-attachable, with no Collector,
  Prometheus, or Grafana host port publication;
- all six runtime services had zero restart counts after rollout;
- only Grafana was recreated for the final configuration hardening, while the
  API, Collector, Prometheus, Caddy, and PostgreSQL containers were preserved;
- fresh Grafana startup logs contained no external plugin-update request or
  outbound update error after both update-check settings were disabled.

The owner-only target records are
`/var/lib/goldsrcops/evidence/observability-rollout.json` and
`/var/lib/goldsrcops/evidence/host-runtime-observability.json`. They contain no
credentials, bearer tokens, private endpoint values, or operator addresses.

## Failure Semantics

Collector, Prometheus, and Grafana are operational dependencies, not
transactional application dependencies. Collector loss can drop telemetry, but
must not make the API unready or stop polling, RCON dispatch, alert persistence,
dead-letter replay, or retention. The API does not wait for the Collector at
startup. Container smoke enforces this by stopping the Collector and rechecking
API readiness.

When telemetry is missing:

1. Check `docker compose ps` and the bounded logs for all three services.
2. Query `up{job="otel-collector"}` and `up{job="goldsrcops"}` in Prometheus.
3. If Collector is down, validate its tracked configuration with production
   preflight before restarting it.
4. If only application series are absent, verify the API OTLP settings and the
   `otel-collector:4317` network path; do not switch Prometheus to `/metrics` as
   an undocumented repair.
5. If Grafana is empty while Prometheus has data, verify the provisioned
   datasource UID `goldsrcops-prometheus` and dashboard UID
   `goldsrcops-operations`.

Configuration and provisioned dashboards are restored from the reviewed
revision. Prometheus history and mutable Grafana state are intentionally not
part of the PostgreSQL recovery contract. Their loss must be recorded, but it
does not block restoration of GoldSrcOps durable workflows.
