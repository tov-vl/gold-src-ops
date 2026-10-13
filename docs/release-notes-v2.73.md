# v2.73 - Public Web API warnings

Pending configuration-only acceptance.

Prometheus warns when one public API operation has at least 10 estimated reads,
5 failures and a 20% failure ratio over 15 minutes, sustained for five minutes.
Caller cancellation and optional 404 do not count as failures. The existing
Grafana panel shows pending/firing warnings, window volume and diagnostic steps.

No traffic and absent telemetry remain unknown rather than an availability
claim. Latency warnings and personal notifications are not introduced. Runtime
images, game, database, secrets and ports remain unchanged.

See [readiness](v2.73-readiness.md) and the [operator guide](web-public-api-alerts.md).
