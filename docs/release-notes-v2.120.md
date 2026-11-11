# v2.120 - Archive health in the Operator report

Adds Archive observation freshness, scheduled-success age and the four existing
rule states to the existing downloadable Operator health report. Game-host and
Archive timestamps and failures remain independent; unavailable or stale data
is explicit Unknown/Stale. The static UTF-8 attachment retains its existing
authorization, filename and cache protections.

This is a Web-only R1 release using the existing v2.118 API. API, game, agent,
Archive writes, scheduling, thresholds, credentials and permissions stay outside
this slice. See [readiness and claim limits](v2.120-operator-archive-report.md).

An isolated R3 browser-CI recovery runs the complete built browser group offline
in the matching digest-pinned official Playwright image. Audited host restore
and build remain; runtime APT/browser installation is unnecessary. Production
DNS and all required tests stay intact. Earlier failures occurred before tests.

Released and installed: [v2.120.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.120.0).

Required CI, independent publication/runtime/actual Operator download acceptance
and same-digest stable promotion passed. Only Web was recreated; 14 other
captured containers and monitoring configuration were preserved.
