# v2.131.0 - Operator command dispatcher health

Adds the existing dispatcher cycle metadata and three warning states to an
Operator-only API, detail page, eighth overview card and the bounded UTF-8
health report. Empty-queue progress and individual RCON failures retain their
existing semantics. Sources keep independent freshness, cancellation and failure
handling; an unavailable source does not hide the others.

R1 delivery targets API and Web only. Command execution, monitoring policy,
credentials, game/agent and Archive cadence are unchanged. Required CI, immutable
publication and independent API/Web acceptance passed.
The authenticated page, explicit refresh, eight-card overview and UTF-8 download
were accepted. Thirteen other containers and protected configuration were
preserved; stable promotion reused candidate bytes without rebuilding. See the
[readiness record](v2.131-operator-command-dispatch-health.md).
