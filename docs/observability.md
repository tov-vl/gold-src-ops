# Production Observability

## Status

The repository-side v2.3 observability contract is implemented. The reference
Compose topology, immutable image references, Collector and Prometheus
configuration, Grafana provisioning, preflight assertions, and container smoke
coverage are ready for target deployment. Live target evidence remains pending.

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

Anonymous access and self-registration are disabled. The reference deployment
does not expose Grafana through Caddy. Open an SSH tunnel only for the operator
session that needs it:

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
