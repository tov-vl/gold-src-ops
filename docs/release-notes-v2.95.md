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
passed, including Linux tests, container and browser smoke. The R3 installation
follow-up was accepted through [PR #409](https://github.com/tov-vl/gold-src-ops/pull/409)
and [PR #410](https://github.com/tov-vl/gold-src-ops/pull/410).

The R3 follow-up supplies a hash-bound agent-only package/upgrade, exact-reference
rollback, explicit fresh queue initialization and a durable pending gate for
scheduled capture. The persistent guard, game payloads, credentials and queue
schema are preserved. Fresh activation uses an explicit initializer; status
continues to refuse a missing queue.

[Agent v2.95.0](https://github.com/tov-vl/gold-src-ops/releases/tag/game-event-agent-v2.95.0)
was published and independently accepted in production on 2026-10-29. Both idle
loops advanced, queue/source continuity and passive-read database bytes were
preserved, and A2S plus persistent/backup guards passed. Only the agent was
stopped/started; game invocation, credentials, addons and container runtimes
were preserved. The old release remains available and rollback was not needed.
Stable assets reuse the exact accepted candidate bytes without rebuilding.

This is a separate agent release; the control-plane release remains v2.94.0.
No real traffic, client rendering or 24-hour observation claim is made. See
[installation](game-event-agent-upgrade.md) and [evidence](v2.95-agent-health.md).
