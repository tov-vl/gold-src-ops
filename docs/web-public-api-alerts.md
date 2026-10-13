# Public Web API read warnings

## Policy

Prometheus evaluates `PublicWebApiReadFailures` separately for `status`, `history`,
`leaderboard`, and `server`. Over a rolling 15-minute window all three conditions
must hold continuously for five minutes:

- estimated completed reads >= 10;
- estimated `error` + `timeout` reads >= 5;
- failures / completed reads >= 20%.

`success` and optional `not_found` count in the denominator. Caller `canceled`
reads count in neither numerator nor denominator. Unknown operations/outcomes
and other services are excluded. `increase` is applied before summation across
instances, so a counter reset is not a burst of errors. Prometheus extrapolates
window increases; these are estimates, not exact request counts. A first sample
cannot reveal failures before that sample. These are initial pilot thresholds,
not an SLO. Latency stays on the p95 graph without a new warning threshold.

The rule uses the histogram from [v2.72](v2.72-public-api-observability.md).
Recording rules `goldsrcops_web_public_api:reads_15m` and
`goldsrcops_web_public_api:failures_15m` expose the window totals. The alert has
only operation, severity and component labels. Grafana displays Prometheus
`ALERTS` (pending/firing) on `goldsrcops-web-public-api`; it does not manage the
rule or send notifications. No Alertmanager destination is added.

## Interpreting missing or recovered warnings

No traffic, canceled-only traffic and absent observations do not trigger this
warning. They also do not prove that the site or telemetry is healthy. Collector
can retain unchanged counters; they stop contributing increases as the window
advances. Missing scrapes or export eventually age failures out of the window.
Consequently a cleared warning can mean successful reads, idle traffic or lost
telemetry. Check evidence before calling it recovery. Recent errors from an old
Web instance remain part of the 15-minute history, even after replacement.

## Diagnosis

1. Follow the warning's `runbook_url` to this guide and open dashboard
   `goldsrcops-web-public-api`. Check the operation,
   pending/firing state, estimated read volume, outcome counts and failure ratio.
2. Read the affected public page once. Check Web/API health and the Collector
   scrape target. Check whether counts advance; an old constant series or no
   active warning is not a heartbeat. The fixed Web export interval is 60 seconds.
3. Separate API errors from timeouts using the outcome panels. Inspect bounded
   server logs and dependency health without copying payloads or credentials.
   Optional `not_found` is expected absence; caller cancellation is not failure.
4. After a repair, require fresh successful reads and healthy telemetry. Allow
   the 15-minute window to age out; five minutes is the activation hold, not a
   promised recovery time. Do not force failures, restart the game, replay RCON,
   or send manual backup commands merely to clear this warning.

Loss of Prometheus/Grafana itself is outside this passive path. Independent
availability evidence and personal notification delivery remain separate.

## Installation and recovery

R3 configuration-only scope: one added rule file, one rule-file entry in
`prometheus.yml`, and the existing Web dashboard. No application/image, network,
secret, port, database, game or backup changes. Record the reviewed commit and
SHA-256 of all three deployed files; no application release tags/images are
needed. Preserve previous bytes, modes, owners, all container identities and
other monitoring configuration before installation.

Validate configuration and synthetic policy with the pinned production images:

```text
python tools/smoke/player-data-monitor-stack.py
```

This includes warning hold/firing/resolution cases and isolated in-place config
reload, removal of the new group, invalid-reload rejection and restoration.

Install the new rule first and validate it with the running image's `promtool`.
The production config is an individual bind-mounted file: preserve its inode
when writing the reviewed content, then run `promtool check config` and send
SIGHUP to that exact Prometheus container. Keep reload HTTP endpoints disabled.
Publish the dashboard through its existing directory mount. Verify exact bytes,
reload success, healthy rule evaluations, provisioned panel queries, existing
rule preservation and unchanged container start times/mounts. No production
failure injection is required; synthetic firing is independent evidence.

If acceptance fails, restore the saved config bytes in place and dashboard,
validate and reload, and confirm the new rule group is absent. Then remove only
the newly installed rule file whose hash matches the reviewed artifact. Preserve
Prometheus history, Grafana state and all unrelated files. An uncertain write or
reload needs readback before any retry. Keep private rollback data outside Git.
