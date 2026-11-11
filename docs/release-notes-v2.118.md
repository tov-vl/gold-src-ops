# v2.118 - Archive health in the Operator portal

Adds a separate Operator-only Archive page with successful scheduled-run age,
observer freshness, four existing warning states and fixed troubleshooting
guidance. Static Refresh uses the existing server-side BFF and bounded private
Prometheus reads. Unknown/Stale states do not imply healthy scheduling.

API/Web-only R1 delivery. No archive write, schedule/threshold change, new
credential/permission, game/agent restart or database migration. Publication and
production acceptance are pending; see [scope and evidence](v2.118-archive-health.md).
