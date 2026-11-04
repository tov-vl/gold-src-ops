# v2.105 - Game-host RAM and CPU monitoring

Add passive host RAM/swap and approximately one-second CPU/steal samples once
per minute through the existing private telemetry path. RAM uses MemAvailable;
CPU excludes idle/iowait/steal from executed work and rejects regressing
counters. Missing groups and stale observations stay unknown.

A private Grafana dashboard and three five-minute warnings cover unavailable
monitoring, invalid observations and low RAM (below 10% or 256 MiB). Existing
IRM/email routing is extended by exact warning tuples. CPU and swap remain
observational without thresholds. There are no game/agent restarts, automatic
remediation, player-data changes or application/addon releases.

Focused resource, policy, dashboard lifecycle, complete private telemetry and
notification routing/privacy checks passed locally. Product PR #473, full
required PR/main CI, signed candidate publication and independent target
acceptance passed. The owner explicitly approved the fixed read capability.
Twenty-two ordinary timer observations, nine current dashboard queries and
three healthy warning definitions passed while old runtime, data, guards and
monitoring were preserved.

One operator-only canary startup error was reconciled before any alert POST.
Original evidence was retained; a corrected separately claimed execution fired,
resolved in IRM and stopped. Provider email sending was confirmed; inbox receipt
and a separate recovery email are not claimed. The historical backup warning
remains visible.

Signed [stable v2.105.0](https://github.com/tov-vl/gold-src-ops/releases/tag/game-resource-monitor-v2.105.0)
reuses accepted candidate source and ZIP/checksum bytes without rebuilding.
Application latest remains v2.94.0 and addon 0.39.0 is unchanged.
See the [scope and acceptance evidence](v2.105-game-resource-monitor.md).
