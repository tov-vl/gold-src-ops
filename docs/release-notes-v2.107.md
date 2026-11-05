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

Released on 2026-11-05. [Product PR #477](https://github.com/tov-vl/gold-src-ops/pull/477)
and full required PR/main CI passed. The signed
[stable package](https://github.com/tov-vl/gold-src-ops/releases/tag/game-health-overview-v2.107.0)
reuses the independently downloaded, accepted candidate ZIP at source
`68520d65302a71bbbb9beb534864cc2ac655f3a2`. Independent live acceptance covered
all 22 queries and preserved all ten containers, six existing dashboards,
policies, worker flags, latest capture and historical backup warning. No
restart, rollback or additional notification canary was required.

Local checks covered 32 PromQL scenarios, six lifecycle cases, 159 existing
policy scenarios, the real telemetry/Grafana stack and unknown-state browser
rendering. Canonical Git packaging fixed a CRLF discrepancy detected before
publication. See the [scope, reading guide and evidence](v2.107-game-health-overview.md).
