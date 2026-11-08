# v2.114.0 - Credential expiry metadata

Status: released and accepted on 2026-11-08.

Operators gain a private read-only register of expiry and last-verification
dates for four existing service-access purposes. Exact deadline, seven-day and
30-day warnings are distinct from Unknown metadata. Labels are fixed; secret
values, identifiers and references are excluded. Dates describe a recorded check,
not current provider validity or successful rotation.

The page works without JavaScript through the existing server-side BFF and
Operator policy. Only API/Web and their date-only configuration mapping change.
Database, game, agent, monitoring rules, IRM/email, provider settings and
permissions are unchanged.

[Product PR #504](https://github.com/tov-vl/gold-src-ops/pull/504), PR/main CI,
all 12 candidate jobs, independent registry/runtime checks and actual Operator
acceptance passed. All 13 other captured containers were preserved. Signed
stable promotion passed all 12 jobs and independently reused the accepted
candidate digests without rebuilding. The
[GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.114.0)
is published.

Two Grafana expiry/check date pairs were verified. B2 metadata remains Unknown
pending provider verification. Warning transitions and failure states are fixture
coverage, not live expiry or credential-validity tests. See
[evidence, inspection exception and claim limits](v2.114-credential-expiry.md).
