# v2.104 - Game-host storage monitoring

Adds passive per-role available bytes and inode observation, a private Grafana
dashboard and four held warnings through the existing IRM/email channel. Strict
freshness and availability gates prevent stale or unsupported data from appearing
healthy or full. Existing agent/backup diagnostics and notification contracts are
preserved. No automatic file cleanup, game/agent restart or addon change.

The owner-approved fixed `storage-status` read capability, dedicated timer,
dashboard and warning routes are installed and independently accepted. Fresh
ordinary timer observations and seven exact dashboard queries passed; existing
processes, queue/spool, backup journal and monitoring were preserved.

Signed [stable v2.104.0](https://github.com/tov-vl/gold-src-ops/releases/tag/game-storage-monitor-v2.104.0)
reuses the accepted candidate ZIP/checksum without rebuilding. The separate
control group fired and automatically resolved in IRM. Provider email sending
was confirmed; inbox receipt, a separate recovery email and live nVault byte
identity are not claimed. See [scope and evidence](v2.104-game-storage-monitor.md).
