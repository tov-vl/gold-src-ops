# Post-release operational check - 2026-10-13

Read-only production inspection at 16:58-16:59 Europe/Moscow after v2.74.
Repository baseline: `c8a64cebe974bc40eb939d2c2146373343b70ca3`.
Scope: public Web read telemetry, existing Grafana panels and scheduled
player-data backup evidence. D0 documentation closure; no new application
release, deployment, restart, injected failure or backup execution.

## Web and telemetry

- Seven services were running; every configured health check was healthy and
  all restart counters were zero. Container identity, image and start-time
  fingerprints were unchanged between observations.
- Both Prometheus scrape targets were up with recent scrapes and no scrape
  errors. All three Web and seven backup recording/alert rules were healthy.
  No pending/firing Web or backup warning was observed.
- Grafana datasource health was OK. Both dashboards were provisioned; all
  eight Web and ten backup panel queries executed successfully. This verifies
  provisioning and queries, not a new visual review of Grafana.

Before the controlled reads, the rolling 15-minute window contained zero new
Web API reads for all four operations. The one-hour window contained about one
estimated successful read each for `status`, `history` and `server`, and zero
for `leaderboard`. No error/timeout series was observed in either window.
These `increase` values are extrapolated estimates, not exact request counts.

The 15-minute p95 values were unavailable. The one-hour histogram estimate for
three operations was 9.5 ms, based on about one read each; this is insufficient
for a useful performance conclusion. It is API-read duration, not page-load
time. There is no evidence here to justify a latency optimization or to claim
normal-load reliability. Missing warnings alone do not prove healthy export.

One GET each to `/`, `/play` and `/leaderboard` returned HTTP 200. After 64
seconds, successful-read counters increased as follows:

| Operation | Counter increase |
| --- | --- |
| status | 1 |
| history | 1 |
| leaderboard | 1 |
| server | 2 |

This confirms new successful reads reached Prometheus for every operation.
These controlled reads are acceptance traffic, not evidence of player adoption.
No application error was injected and no warning threshold was changed.

## Player-data backup

The read-only monitor inspect validated the current ledger and manual-copy
registry. A fresh verified-copy record is present, sourced from a manual
capture at **2026-10-11 15:32:26 Europe/Moscow**, about 49.43 hours old at the
first observation. This pass verified the accepted record and freshness; it
performed no new repository readback, restore or engine recovery exercise.

The last weekly slot is **2026-10-11 04:00 Europe/Moscow** with state
`reconciled-no-capture` (monitor code 8). It did not create a weekly copy.
There is no pending operation, reconciliation requirement, failed service,
missed-cycle flag or active backup. The backup service is inactive with a
successful current result; that result does not turn the reconciled slot into
a successful capture.

The weekly timer is enabled and waiting. Its next invocation is
**2026-10-18 04:00 Europe/Moscow**, subject to the existing empty-server policy.
The monitor timer is enabled; observed heartbeat ages were about 52 and 55
seconds, with its observation timestamp advancing between samples.

## Outcome and remaining gate

No runtime repair is justified by this check. The pending first successful
weekly-cycle acceptance remains open. After an actual scheduled attempt,
inspect its result and capture age: `verified` can close the byte-level weekly
cycle gate; `skipped-busy` or `reconciled-no-capture` cannot. No manual retry or
new scheduled notification was created by this inspection.

Game visual acceptance and two-player play remain separate/deferred. Private
operator evidence is owner-restricted; no credentials, snapshot identifiers,
private topology, player records or raw ledger contents are tracked here.
See [Web warnings](web-public-api-alerts.md),
[weekly backup](game-player-data-schedule.md) and
[backup monitoring](game-player-data-monitoring.md).
