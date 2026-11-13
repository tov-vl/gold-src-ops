# v2.129.0 - Retention in the Operator health report

Adds snapshot-retention cycle metadata and the three existing warning states
to the bounded Operator-only UTF-8 diagnostic attachment. All seven sources
retain independent freshness, failure isolation and caller cancellation.
The overview download text now matches its seven-source content.

Only Web is an installation target. Retention behavior/settings, monitoring,
permissions, credentials, game/agent and the hourly Archive cadence are unchanged.
Released and installed after required CI, immutable publication and independent
Web-only runtime and authenticated download acceptance. Stable reused accepted
digests without rebuilding; fourteen other containers were preserved. See the
[readiness record](v2.129-retention-health-report.md).
