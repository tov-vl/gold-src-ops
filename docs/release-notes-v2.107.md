# v2.107.0 - Game-host health overview

Adds one private Grafana overview of agent/queue/spool, backups, storage/inodes,
RAM/swap, sampled CPU/steal and current Prometheus warning states. Independent
freshness prevents missing data from appearing healthy. Manual backup evidence
and weekly attempt history remain separate. Detail dashboards and runbooks are
linked without publishing infrastructure identity or credentials.

The three-file operations package uses the existing claimed create/rollback
protocol. No new runtime service, permission or public port; game, agent,
existing dashboards, alert policies and external IRM configuration remain
unchanged. CPU is observational, and local metrics do not prove IRM receipt.

Delivery and target acceptance are pending. See the
[scope, reading guide and gates](v2.107-game-health-overview.md).
