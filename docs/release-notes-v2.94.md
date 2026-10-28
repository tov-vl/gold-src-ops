# v2.94.0 - Public status freshness

The public fleet summary evaluates every enabled server using its own polling
interval. A recent response from one server no longer hides another server's
expired Online observation. Only fresh Online observations count as online.
The summary is unknown when no enabled observation is fresh, and degraded when
fresh observations coexist with stale or non-Online servers or open incidents.

Public JSON and database schema are unchanged. This API-only read correction
does not change incident detection, polling, gameplay or stored server state.
The existing public output-cache budget remains 15 seconds.

Published as [v2.94.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.94.0)
from `7b10ca8d6f6bde775b5d911f34b36c186333ddfa` through
[PR #403](https://github.com/tov-vl/gold-src-ops/pull/403).
[Candidate CI](https://github.com/tov-vl/gold-src-ops/actions/runs/40761043470)
and [stable promotion](https://github.com/tov-vl/gold-src-ops/actions/runs/40764449235)
passed, including independent verification of all three published images.
Stable references reuse the candidate digests without rebuilding.

Only API was installed. Three production read-only samples over 196 seconds
passed health, public status RU/EN, fresh reachable A2S, empty command queue and
Prometheus checks. The other eight containers and durable mounts were preserved;
rollback was not needed. Web remains v2.79, receiver remains v2.22 and the game
addon is unchanged. Negative stale/mixed-state cases were verified in tests;
production data was not altered to manufacture them. Interactive identity,
gameplay and long-term SLO acceptance are outside this release. See
[evidence and rollback](v2.94-status-freshness.md).
