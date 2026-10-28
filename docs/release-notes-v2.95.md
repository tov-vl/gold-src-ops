# v2.95 - Agent diagnostic contract

The game-event agent gains passive queue status, observation time, oldest
pending/in-flight age, spool availability and separate import/delivery cycle
evidence. Existing status JSON schema-1 fields are preserved. Missing or
unreadable queue state fails closed; no queue is created or migrated by status.

Idle, stale, stopped, failed, retrying and blocked evidence can be distinguished
without relying on the arrival of player events. Workers keep their existing
delivery and error semantics; diagnostic write failures do not control them.

Implemented through [PR #407](https://github.com/tov-vl/gold-src-ops/pull/407),
merge `eb48ffaf8a1f67cf88c069c9aaf30c490e53f820`.
[Required PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/40770981239)
passed, including Linux tests, container and browser smoke. Implementation,
post-merge CI and production installation remain separate evidence. No stable
runtime release or production upgrade is claimed. The active hash-bound pilot
bundle needs a separately reviewed agent-only upgrade/rollback workflow. See
[scope, verification and delivery limits](v2.95-agent-health.md).
Consumers that used status to initialize a missing queue must move that write
into an explicit guarded installation step; fresh activation is part of the
pending installation gate.
