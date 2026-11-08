# v2.113.0 - Operator health report

Released as [v2.113.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.113.0).
Candidate publication, independent runtime/Operator download acceptance and
same-digest stable promotion passed.

The private game-host health page gains a "Download health report" link. It
fetches a new bounded snapshot and returns a plain-text UTF-8 attachment with
generation/evaluation times, component observation ages, measurements and the
existing warning guidance. Download works without JavaScript and retains the
Operator-only server-side token boundary.

Unknown, Stale and fresh empty warnings have explicit separate meanings. The
report writes fixed labels and sanitized measurements rather than raw provider
descriptions, host data or errors. Responses are no-store; no report archive is
created on the server.

Only Web was installed; the accepted v2.111 API and v2.112
rollback Web remain compatible. No database, game, agent, monitoring rule,
notification, credential or permission change is included.
All 14 other captured containers were preserved. Stable references reuse the
accepted candidate images without rebuilding.

Acceptance, publication and rollout evidence is recorded in the
[milestone record](v2.113-operator-health-report.md). No RPO/RTO, availability
guarantee or live-fault acceptance is implied.
