# Player search integration rehearsal

This opt-in rehearsal joins the actual Web, API, PostgreSQL repository and UDP
RCON transport to a disposable ReHLDS/AMXX server. It complements the focused
contract, engine and browser tests from the [addon](player-substring-addon.md),
[API](player-substring-api.md) and [Web](player-substring-web.md) slices.

## Boundary and reproduction

Run on Windows with Docker Desktop Linux containers, Python 3, the repository
.NET SDK and restored local dotnet-ef tool. Build the current solution in Debug
first. Restore the pinned local tool with `dotnet tool restore`.

Prepare the pinned AMXX/ReAPI compiler inputs and the seven engine assets from
[the feasibility record](player-substring-search-feasibility.md#reproduction-and-evidence-identity).
The input directory must contain `amxx-1.10.0.5481`, `reapi-5.24.0.300`, `assets`
and `extracted`. The runner verifies the engine archives and extracted ZIP
contents, then compiles the seed, current product and pre-v2 product itself.
It does not download engine inputs or accept a remote endpoint or credentials.

```powershell
python tools/smoke/player-search-integration.py --inputs artifacts/player-substring-research
```

Each run creates a unique evidence directory under `artifacts`, a dedicated
Docker bridge network, game and PostgreSQL containers, and local API/Web
processes. Published game/database ports and HTTP listeners bind only to
127.0.0.1. The game image is digest-pinned in the runner; PostgreSQL uses the
local `postgres:17-alpine` image. The database lives in tmpfs. Existing migrations
are explicitly applied only to this new database; normal startup does not migrate.
Background polling, command dispatch, retention, alert delivery and leaderboard
polling are disabled. All credentials and records are synthetic fixture values.

The standalone seed plugin runs before the product and creates 16 statistics
records and 64 preference records: names, language/weapon settings, HUD settings
and streaks. It is never loaded alongside the product. Product lookup and old
addon rollback run against these same vault files. The runner closes the engine
before hashing both vaults, including their record timestamps. The previous
addon source is pinned to `5f520a5a46e046c2a8212f7893adc033c77419e5`.

A `finally` block stops owned processes, removes the two owned containers and
network, and removes only the task-created publish directories. Logs, compiled
fixtures and `result.json` remain in the ignored evidence directory. Those logs
contain synthetic RCON packets and must not be used as a production log template.
The runner does not inspect, modify or clean pre-existing containers or data.

## Acceptance matrix

- Real HTTP exact lookup, including a legacy request without `match`, returns
  duplicate stored names; contains returns distinct matching names and the
  ten-row truncation marker.
- The same query has independent exact/contains cache entries. A different mode
  cannot bypass shared API admission. Restarting only the API confirms the addon
  still enforces the shared cooldown without the API cache.
- RU/EN static SSR forms use real antiforgery cookies and POSTs through Web to
  the API. Selected modes survive fresh and busy responses. No JavaScript is
  needed for this transport rehearsal.
- Short exact names, Russian case folding, four-byte UTF-8 names, empty results
  and HTML escaping traverse the real transport.
- Disabled addon lookup becomes unavailable, with shared admission on subsequent
  attempts. The new API with addon 0.25.0 retains exact compatibility and rejects
  unsupported v2 as unavailable.
- Engine console logs must contain exactly the expected v1/v2 RCON command
  counts. Cache hits, busy admission and unsupported versions cannot silently
  introduce retries or fallback commands.
- Current addon queries, rollback to 0.25.0, and reopening with 0.26.0 preserve
  both closed vault files byte-for-byte. AMXX runtime error logs are absent.

This is local synthetic integration evidence. It does not establish browser
layout, JavaScript countdown behavior, production performance or release safety
on a deployed host. Browser behavior has separate prior-stage evidence. Required
remote CI, immutable publication and production acceptance remain separate gates.

## Local result, 2026-10-15

The complete transport matrix passed twice against product revision
`774e8da6b0144ae7e2ae40a155eede75dc5cdcf8`. The final run also enforced the exact
command-count assertions and recorded all three helper source hashes, compiled
plugin hashes, Docker image IDs and both vault hashes in its local `result.json`.
All nine acceptance groups passed in 181.133 seconds, including setup and the
intentional ten-second cooldown waits. This is not a search latency benchmark.

| Engine phase | v1 commands | v2 commands |
| --- | --- | --- |
| Current addon | 4 | 7 |
| Previous addon | 1 | 1 |
| Reopened current addon | 0 | 1 |

All 80 seeded records remained covered by the two unchanged vault hashes after
queries, rollback and reopen. The API cache was restarted independently during
the addon cooldown check. Duplicate exact names, ten-row partial truncation,
RU/EN forms, short exact input, Russian/emoji names, empty results, HTML escaping,
no-store/no-referrer headers and public identity boundaries passed. No AMXX error
log was produced. Current, previous and seed plugins compiled without warnings.
Python syntax, shell syntax, changed-documentation links, whitespace and authored
text checks passed. Owned Docker containers and network were absent after cleanup.

Early setup attempts exposed three harness issues: this Docker configuration did
not publish ports on its internal network, the pinned local EF tool needed
restore, and the synthetic credential row initially used the wrong enum text.
Those were corrected in the fixture setup; product code did not need a change.
The dedicated bridge binds published ports only to loopback. Local images must
already exist; container startup uses `--pull never`.

The full .NET Quality Gate was not repeated for these tooling/documentation-only
changes. The unchanged product revision has the prior Web-stage result of 1247
passing tests and five separately passing browser scenarios. This rehearsal is
opt-in and does not add another required CI job or extend Container Smoke.
Remote main was freshly checked at `5f520a5a46e046c2a8212f7893adc033c77419e5`;
the combined local implementation has not been pushed. Required CI and the
separately authorized addon/API/Web release remain pending.
