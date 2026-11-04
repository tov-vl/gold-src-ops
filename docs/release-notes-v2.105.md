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
notification routing/privacy checks passed locally. Publication and target acceptance
are pending; the new fixed SSH read capability requires an explicit decision.
Stable publication will reuse accepted candidate bytes without rebuilding.
See the [scope and delivery gates](v2.105-game-resource-monitor.md).
