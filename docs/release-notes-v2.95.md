# v2.95 - Agent diagnostic contract

The game-event agent gains passive queue status, observation time, oldest
pending/in-flight age, spool availability and separate import/delivery cycle
evidence. Existing status JSON schema-1 fields are preserved. Missing or
unreadable queue state fails closed; no queue is created or migrated by status.

Idle, stale, stopped, failed, retrying and blocked evidence can be distinguished
without relying on the arrival of player events. Workers keep their existing
delivery and error semantics; diagnostic write failures do not control them.

Implementation, CI and production installation remain separate gates. No stable
runtime release or production upgrade is claimed. See
[scope, verification and delivery limits](v2.95-agent-health.md).
