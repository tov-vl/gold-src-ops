# v2.128.0 - Operator snapshot retention health

Adds a read-only Operator detail page and overview card for snapshot cleanup
progress and the three existing retention warnings. Cycle metadata, data age
and warning evaluation time remain independent; unknown or stale observations
never establish healthy cleanup. Bearer tokens remain on the BFF server.

The six-source health download retains its existing scope. Cleanup behavior,
retention settings, warning thresholds, credentials, game/agent and the hourly
Archive schedule are unchanged.

Released and installed after required CI, immutable publication and independent
API/Web and authenticated Operator acceptance. Stable reused accepted digests
without rebuilding; thirteen other containers were preserved. See the
[readiness record](v2.128-operator-retention-health.md).
