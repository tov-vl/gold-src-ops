# v2.120 - Archive health in the Operator report

Adds Archive observation freshness, scheduled-success age and the four existing
rule states to the existing downloadable Operator health report. Game-host and
Archive timestamps and failures remain independent; unavailable or stale data
is explicit Unknown/Stale. The static UTF-8 attachment retains its existing
authorization, filename and cache protections.

This is a Web-only R1 release using the existing v2.118 API. API, game, agent,
Archive writes, scheduling, thresholds, credentials and permissions stay outside
this slice. See [readiness and claim limits](v2.120-operator-archive-report.md).

Release and target acceptance are pending.
