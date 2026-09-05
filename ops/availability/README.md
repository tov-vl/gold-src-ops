# Availability Evidence Scheduler

This directory contains the external scheduling wrapper for the v2.4
availability evidence exporter. It runs on a GitHub-hosted runner, outside the
production control-plane and game-server failure domains.

The scheduler is not the `API-01` probe. Grafana Cloud Synthetic Monitoring
continues to produce the one-minute primary population. The workflow exports a
six-hour rolling window once per hour at minute 53, then writes one
content-addressed primary segment to the independent B2 archive. The overlap
allows later runs to recover from ordinary GitHub scheduling delays without
changing the canonical slot rules.

GitHub documents that scheduled workflows may be delayed or dropped and that
inactive public repositories have scheduled workflows disabled after 60 days.
Missing scheduler runs therefore require operational review; they do not create
or erase primary probe samples. A later overlapping export may fill an archive
gap while the provider still retains the source metrics.

## GitHub Environment

Create an environment named `availability-evidence-shadow` with deployment
history disabled for the workflow job and restrict it to the protected `main`
branch. Do not add a required reviewer because scheduled runs are unattended.

Leave the repository variable
`GOLDSRCOPS_AVAILABILITY_SCHEDULER_ENABLED` absent or set to `false` during
setup. Scheduled jobs remain skipped until this variable is explicitly set to
`true` after the manual archive/recovery proof succeeds.

Set this environment variable:

```text
GOLDSRCOPS_AVAILABILITY_EXPORTER_REVISION=<reviewed 40-character main commit>
```

The workflow checks out that exact revision and fails when the effective source
does not match. Advancing the revision is a reviewed operational action and must
not happen during an active evidence window without recording a new evaluator
revision.

Set these environment secrets without printing them in workflow logs:

```text
GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE
GOLDSRCOPS_GRAFANA_METRICS_URL
GOLDSRCOPS_GRAFANA_METRICS_USER
GOLDSRCOPS_GRAFANA_METRICS_TOKEN
GOLDSRCOPS_B2_S3_ENDPOINT
GOLDSRCOPS_B2_REGION
GOLDSRCOPS_B2_BUCKET
GOLDSRCOPS_B2_WRITE_KEY_ID
GOLDSRCOPS_B2_WRITE_APPLICATION_KEY
GOLDSRCOPS_B2_READ_KEY_ID
GOLDSRCOPS_B2_READ_APPLICATION_KEY
```

The Metrics API token remains `metrics:read`. The B2 reader and writer remain
separate, bucket- and prefix-scoped, and time-bounded where supported. The job
maps secrets only into the export step; checkout, SDK setup, restore, and publish
steps do not receive them.

Optional transport-failure enrichment maps these additional environment
secrets only into the export step:

```text
GOLDSRCOPS_GRAFANA_LOGS_URL
GOLDSRCOPS_GRAFANA_LOGS_USER
GOLDSRCOPS_GRAFANA_LOGS_TOKEN
```

Use a separate access policy with only `logs:read`, scoped to the reviewed
stack and `job=~"goldsrcops-api-.*"`. Create a 90-day token. Supply the Loki
HTTPS base URL and Logs tenant user from the stack's connection details.
Do not reuse the Metrics API credential. All three secrets must be set
together; absent settings preserve metrics-only behavior, and partial settings
fail closed in the exporter.

Before enabling log enrichment, verify the credential with a bounded query and
a temporary diagnostic check against a deliberately unresolvable `.invalid`
hostname. Give it a separate job and monitor revision, no production target,
one probe, and a short lifetime. Confirm `dns_error` in the normalized output,
then disable the temporary check. Keep its evidence out of the primary archive.
This proves DNS classification only; other transport categories remain separate
live checks. A healthy primary export does not query Loki and cannot prove log
access.

After that proof, review the workflow mapping, advance the environment's
exporter revision to the reviewed main commit containing the Loki adapter, and
verify one serialized cycle. Record the effective revision before opening a new
shadow window. Keep secrets unset and the previous pin while credential setup
or live verification is incomplete.

## Operation

The workflow serializes scheduled and manual runs through one concurrency group
with cancellation disabled. It performs exactly one `archive` attempt and never
uploads raw JSONL as a GitHub artifact. The runner removes its temporary segment
whether the cycle succeeds or fails.

Use `workflow_dispatch` only for a reviewed catch-up window and set
`confirm_archive` to acknowledge its single remote write. `window_end_utc` must
be an exact UTC minute no newer than the five-minute ingestion boundary;
`lookback_minutes` is limited to 60 through 1,440 minutes. A blank end time uses
the latest eligible minute.

Do not blindly rerun an archive after a timeout or other unknown upload outcome.
Use the read-only `rehearse` command with the expected digest to determine
whether the object committed. Do not run a manual writer while the scheduled
workflow owns the namespace.

Before starting a 24-hour shadow:

1. review the pinned exporter revision and effective monitor revision;
2. execute one manual cycle and confirm the off-host object by read-only
   recovery;
3. set `GOLDSRCOPS_AVAILABILITY_SCHEDULER_ENABLED` to `true` and verify the next
   scheduled cycle;
4. verify that a failed workflow reaches the intended notification path;
5. record the shadow start only after transport and alert-route tests pass.
