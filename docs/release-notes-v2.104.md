# v2.104 - Game-host storage monitoring

Adds passive per-role available bytes and inode observation, a private Grafana
dashboard and four held warnings through the existing IRM/email channel. Strict
freshness and availability gates prevent stale or unsupported data from appearing
healthy or full. Existing agent/backup diagnostics and notification contracts are
preserved. No automatic file cleanup, game/agent restart or addon change.

Implementation and verification gates are tracked in
[scope and evidence](v2.104-game-storage-monitor.md). Production installation
requires the explicit fixed `storage-status` read capability; it is pending.
