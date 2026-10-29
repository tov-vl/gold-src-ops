# v2.96 - Passive agent monitoring

The operations package connects v2.95 aggregate agent diagnostics to the existing
private Collector, Prometheus and Grafana. A once-per-minute observer reports
queue/spool counts, waiting age, service state and independent import/delivery
cycle evidence through one fixed restricted SSH command.

The new dashboard hides expired or invalid current facts, distinguishes idle work
from a stalled loop, and retains valid historical observations. Failed reads do
not become healthy zero-count queues. No player identity or event payload enters
telemetry. Existing game, agent, addon and application binaries are preserved.

The [stable operations package](https://github.com/tov-vl/gold-src-ops/releases/tag/agent-monitor-v2.96.0)
was installed and independently accepted on 2026-10-29 after explicit permission
for the new read capability. Two advancing telemetry observations, real
Prometheus/Grafana facts, restricted-command checks, queue continuity and existing
runtime preservation passed. Product PR #412 and independent required/post-merge
CI are accepted. Stable assets reuse the exact candidate ZIP without rebuilding;
rollback inputs remain retained and rollback was not needed.

See [scope and evidence](v2.96-agent-monitoring.md). This is an operations-only
release; the global application release remains v2.94.0.
Real event delivery, client rendering, direct alerts and 24-hour activity remain
separate observations.
