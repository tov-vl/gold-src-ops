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

Release preparation is in progress. See [readiness](v2.127-retention-liveness.md)
for semantics, rollback and the independent acceptance boundary.
