# GoldSrcOps v2.58 - Leaderboard Freshness Monitoring

Candidate `v2.58.0-rc.1` and stable `v2.58.0` are pending verification/publication.

- API records last successful update, snapshot availability/age and bounded
  reading outcomes without player identifiers or raw errors.
- Provisioned Grafana dashboard shows freshness, passes and failure reasons.
- Prometheus warnings distinguish unavailable data, stale data, stalled worker
  and absent observations, with five-minute pending and bounded busy allowance.
- Existing public leaderboard, gameplay, saves, schema and backup are unchanged.
  Personal notification delivery remains deferred.

R3 runtime scope is API and monitoring. Exact publication, production acceptance,
recovery limits and skipped checks are recorded in
[readiness](v2.58-readiness.md) and [the contract](v2.58-leaderboard-monitoring.md).
