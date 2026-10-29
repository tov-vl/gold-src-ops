# v2.96 - Passive agent monitoring

The operations package connects v2.95 aggregate agent diagnostics to the existing
private Collector, Prometheus and Grafana. A once-per-minute observer reports
queue/spool counts, waiting age, service state and independent import/delivery
cycle evidence through one fixed restricted SSH command.

The new dashboard hides expired or invalid current facts, distinguishes idle work
from a stalled loop, and retains valid historical observations. Failed reads do
not become healthy zero-count queues. No player identity or event payload enters
telemetry. Existing game, agent, addon and application binaries are preserved.

Local verification and delivery gates are recorded in
[scope and evidence](v2.96-agent-monitoring.md). Product CI, immutable publication,
permission for the new read capability and production acceptance are pending.
This is an operations-only release; no global application release is promoted.
Real event delivery, client rendering, direct alerts and 24-hour activity remain
separate observations.
