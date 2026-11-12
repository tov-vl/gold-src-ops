# v2.127 - Snapshot retention worker liveness

## Changes

- Independent snapshot-retention cycle progress and configured/disabled metrics.
- Three held warnings for unavailable observation, stalled progress and repeated
  errors through the existing private IRM/email route.
- Five retention panels added to the existing private background-worker dashboard.
- Empty successful cleanup remains healthy; shutdown does not become a failure.

Retention settings, deletion SQL, batch bounds, Archive scheduling, credentials,
public/Operator contracts and game behavior are unchanged.

## Delivery

Released and installed as v2.127.0. [Product PR #534](https://github.com/tov-vl/gold-src-ops/pull/534)
and all required PR/main/candidate/stable checks passed. API/monitoring-only
acceptance retained fourteen other containers and confirmed a later natural
retention cycle, ten same-UID dashboard panels and isolated IRM Resolved.
Stable reused all accepted digests without rebuilding.

Local Quality Gate passed 1901 tests with 68 opt-in browser cases left to the
hosted Browser Smoke gate; final 22 retention cases and 24 rule scenarios passed.
Scoped InspectCode reported zero errors/warnings and three reviewed notes.
See [readiness and evidence](v2.127-retention-liveness.md) for rollback and limits.
