# v2.118 - Archive health in the Operator portal

Released and installed: [v2.118.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.118.0).

Adds an Operator-only Archive page with the latest accepted scheduled-run age,
observer freshness and four existing warning states with fixed troubleshooting
guidance. Refresh works through the server-side BFF without JavaScript.
Missing, invalid or stale evidence is explicit Unknown/Stale.

R1 API/Web rollout and independent runtime/actual Operator browser acceptance
passed. The 13 other captured containers and monitoring configuration were
preserved. Candidate/stable images have identical verified digests; promotion
does not rebuild. No archive write, scheduler/threshold change, key rotation,
new permission, migration or game/agent restart was part of this release.

See [verification and claim limits](v2.118-archive-health.md).
