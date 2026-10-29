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

The [stable operations package](https://github.com/tov-vl/gold-src-ops/releases/tag/agent-alerts-v2.97.0)
was installed and independently accepted on 2026-10-29. Product PR #414 and both
complete required/post-merge workflows passed. The real target preserved config
inode, old rules/dashboards, observer, nine containers and game/queue/key state;
all five holds remain 300 seconds. Policy evaluation advanced and current panel
queries passed without spurious idle warnings. Exact rollback inputs are retained;
rollback was not needed.

Stable assets reuse accepted candidate bytes without rebuilding. This is a
separate operations release; global latest remains v2.94.0. Real event delivery,
client rendering, direct notifications, production fault injection and 24-hour
activity remain separate observations.
