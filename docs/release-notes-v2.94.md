# v2.94.0 - Public status freshness

The public fleet summary evaluates every enabled server using its own polling
interval. A recent response from one server no longer hides another server's
expired Online observation. Only fresh Online observations count as online.
The summary is unknown when no enabled observation is fresh, and degraded when
fresh observations coexist with stale or non-Online servers or open incidents.

Public JSON and database schema are unchanged. This API-only read correction
does not change incident detection, polling, gameplay or stored server state.
The existing public output-cache budget remains 15 seconds.

Release publication and production acceptance are pending; see
[readiness](v2.94-status-freshness.md).
