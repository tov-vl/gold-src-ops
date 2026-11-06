# v2.110 release notes

Operators can inspect game-host monitoring from the main Web application:
five timestamped status cards, bounded queue/backup/storage/RAM/CPU values,
explicit unknown/stale states and links to existing private Grafana details.
The page works without JavaScript and provides no mutation controls.

The API enforces the existing Operator policy. Reader/public projections,
authentication roles, database schema, game addons, agent and warning policies
remain unchanged. Prometheus labels, raw responses and BFF tokens stay off the
browser surface.

See [scope, configuration and readiness](v2.110-operator-game-host-health.md).
Candidate publication, production acceptance and stable promotion are pending.
