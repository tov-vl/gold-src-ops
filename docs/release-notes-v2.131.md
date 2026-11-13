# v2.131.0 - Operator command dispatcher health

Adds the existing dispatcher cycle metadata and three warning states to an
Operator-only API, detail page, eighth overview card and the bounded UTF-8
health report. Empty-queue progress and individual RCON failures retain their
existing semantics. Sources keep independent freshness, cancellation and failure
handling; an unavailable source does not hide the others.

R1 delivery targets API and Web only. Command execution, monitoring policy,
credentials, game/agent and Archive cadence are unchanged. Release verification
is in progress; see the [readiness record](v2.131-operator-command-dispatch-health.md).
