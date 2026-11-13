# v2.130.0 - Command dispatcher liveness

Adds private worker-cycle observations and three held warnings for unavailable
observation, stalled progress and repeated cycle exceptions. Empty queues and
failed individual RCON commands remain normal completed cycles. Dispatch,
recovery, concurrency, retries and timeouts keep their existing behavior.

R3 delivery targets only API and the reviewed private monitoring files. The
existing dashboard and IRM route are extended. Web/receiver, game/agent,
credentials, permissions and hourly Archive cadence are preserved.

Release and independent production acceptance are pending. See the
[readiness and runbook record](v2.130-command-dispatch-liveness.md).
