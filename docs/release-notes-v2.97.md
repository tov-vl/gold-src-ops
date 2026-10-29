# v2.97 - Agent warning policy

Add five in-stack Prometheus warnings for missing observations, invalid status,
worker problems, delayed waiting work and dead-letter/spool problems. Each uses
a five-minute hold and resolves from fresh recovered observations. Idle running
and disabled loops are handled explicitly; stale retained data cannot produce
healthy or data-specific current claims.

A separate Grafana dashboard shows pending/firing states and requires a fresh
rule evaluation. The accepted v2.96 diagnostic dashboard and observer are
preserved. No key, permission, notification destination, queue mutation or game
restart is added. See [policy, diagnosis and evidence](v2.97-agent-alerts.md).

Implementation verification, product CI, immutable package publication and target
acceptance are pending. This is a separate operations release, with no application
image rebuild or global latest-release change. Real event delivery, client
rendering, direct notifications and 24-hour activity remain separate observations.
