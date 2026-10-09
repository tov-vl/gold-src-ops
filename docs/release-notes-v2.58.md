# GoldSrcOps v2.58 - Leaderboard Freshness Monitoring

Stable [v2.58.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.58.0)
promoted from accepted `v2.58.0-rc.2` at
`c2fc858fb73014a1888fbdd204458f95d8d14e5f`, without rebuilding or repeating rollout.

- API records last successful update, snapshot availability/age and bounded
  reading outcomes without player identifiers or raw errors.
- Provisioned dashboard `goldsrcops-public-leaderboard` shows freshness,
  passes and failure reasons. Grafana loaded it without restart.
- Four Prometheus warnings cover unavailable/stale data, stalled passes and
  missing observations, with five-minute pending and bounded busy allowance.
- Current-resource selection prevents retained older API gauges from masking
  new unavailable, disabled or failed observations. Historical rates remain.
- Only API/Prometheus were recreated. Five other services, private ports and
  named volumes/history were preserved. Public contracts, Web, addon, gameplay,
  scoring, saves, schema and weekly backup schedule are unchanged.

941 .NET tests, 26 browser cases, 25 policy scenarios, isolated/target policy
recovery and all required publication gates passed. Independent production
acceptance correlated API and telemetry snapshots and confirmed automatic
successful-pass progression by 60.128 seconds. Existing OTLP export runs every
60 seconds; metrics describe the observed snapshot and can lag the latest API.

The immutable rc.1 was superseded before deployment. Personal delivery, first
weekly backup on 2026-10-11 at 04:00 MSK and deferred game-client/two-player
acceptance remain independent work. See [readiness](v2.58-readiness.md) for
exact digests, anomalies, skipped checks and recovery limits, and
[contract](v2.58-leaderboard-monitoring.md) for the policy.
