# v2.72.0 - Public API read observability

Pending release on 2026-10-13.

- Web measures public API reads through JSON deserialization with fixed operation
  and outcome labels; caller cancellations and optional 404 responses are separate.
- Existing private Collector and Prometheus receive the histogram; Grafana gains
  a compact dashboard for duration, traffic and failures.
- Production Web gains an internal telemetry connection while retaining edge
  egress. No public metrics endpoint, new credentials, alert rules or notifications.

The five-second response budget, page behavior and API contracts are preserved.
See [readiness and acceptance boundary](v2.72-readiness.md).
