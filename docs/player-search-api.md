# Player search API foundation

This started as step 2 of [the exact search plan](player-search-feasibility.md).
The additive [API mode extension](player-substring-api.md) supports both the
[addon v1 protocol](player-search-addon.md) and [addon 0.26.0 v2](player-substring-addon.md).
The website continues to submit its original exact-only request body. The game
server, database schema and deployed configuration are unchanged by API work;
substring selection in Web and the combined release remain separate steps.

## HTTP contract

`POST /api/public/players/search` is anonymous and accepts UTF-8 JSON:

```json
{"name":"Example"}
```

The body is limited to 256 bytes, including for chunked requests, and is read
with a three-second deadline. Only JSON media types with absent or UTF-8 charset
are accepted. Unknown JSON properties, invalid text and names are rejected.
The normalized name must be 1-31 UTF-8 bytes. Trim only surrounding ASCII spaces;
retain case and other Unicode whitespace. Reject invalid scalar sequences,
C0/C1 controls and case-insensitive `STEAM_` substrings.

An omitted `match` means `exact`. Explicit `"match":"exact"` preserves whole-name,
case-sensitive comparison. `"match":"contains"` requires at least three Unicode
scalars and uses the fixed ASCII/Russian case map from the addon v2 contract.
Explicit null, empty, unknown, differently cased or non-string modes are invalid.
There is no normalization, transliteration, wildcard matching or automatic fallback.
For example, `{"name":"abc","match":"contains"}` can match `[TAG]ABC` and returns
that original stored name, not the query. All modes retain the 256-byte body bound.

| HTTP | Meaning |
| --- | --- |
| 200, `fresh` | Valid snapshot, including a successful empty result. |
| 400, `invalid` | Invalid JSON or nickname, without reflecting input. |
| 404 | Search disabled or public server unavailable in host configuration. |
| 408 | Request body deadline elapsed. |
| 413 | Request body exceeds 256 bytes. |
| 415 | Unsupported or malformed content type/charset. |
| 429, `busy` | HTTP admission, lookup cooldown, in-flight lookup or operator queue busy. |
| 503, `unavailable` | Source/credential unavailable, old addon, invalid frame, transport failure or deadline. |

Search responses contain only `state`, `capturedAtUtc`, `hasMore`, `entries`
and nullable `retryAfterSeconds`. Each entry contains `ordinal`, `name`, `rank`,
`kills` and `deaths`. Ordinal is the position in this result, not global standing
or persistent identity. Keep identical rows. `hasMore` requires ten returned rows.
No-match does not prove that a player never joined. Capture time is the lookup
snapshot time, not the last match or durable write time. Rank progress can be
derived later by Web from these same rank/kills values.

All endpoint responses use `Cache-Control: no-store`. Busy responses include a
`Retry-After` header and delay of 1-10 seconds. Infrastructure details and raw
exceptions never appear in the public result. No automatic retry is performed.
Unsupported HTTP methods retain the host's existing authorization fallback;
there is no GET/query-string search route.

## Configuration and dependency boundaries

`PublicPlayerSearch:Enabled` defaults to false. When true, it requires the
existing `PublicServerJoin:ServerId` and a valid public server configuration.
Invalid enabled configuration fails startup. No caller-supplied server ID,
host, port, credential reference or command is accepted. Settings are frozen
for the process lifetime, not reloaded during an in-flight query. No production
configuration is changed by this implementation.

Application owns the shared name validation, lookup port, process-wide admission
and bounded result cache. The singleton service receives the request's scoped
source for the duration of the call; it never retains a scoped repository.
Infrastructure resolves the configured enabled server, checks its operator queue,
uses its configured RCON credential/secret and invokes the fixed lookup command.
No writes, command-history enqueue or new database storage are introduced.
The existing top-ten endpoint and poller are unchanged.

Each command uses a new random 16-character hex nonce and up to 62 hex characters
of name bytes. The strict parser verifies the requested wire version, exact nonce,
mode-specific name correlation, header/trailer agreement, row count, ordinals,
order, rank thresholds, counters,
UTF-8 and a 2048-character ASCII frame bound. Missing names have no fallback.
A complete frame must be younger than 60 seconds, with at most 30 seconds of
future clock skew. An old/disabled addon produces unavailable rather than an
empty result. Existing RCON datagram/byte limits remain in force.

## Admission and cache

- A single named HTTP limiter admits at most 30 requests per fixed ten-second
  window across all callers, before body parsing. Queue length is zero. It uses
  no IP partition or forwarded header, so arbitrary addresses cannot create
  independent buckets. Fixed-window boundaries can admit adjacent bursts; the
  separate game gate below still applies.
- Application permits one in-flight lookup and one new attempt per ten seconds,
  with no waiting queue. The gate is shared by scoped API requests, including
  different names and both modes. Invalid requests do not consume a game slot.
  Any admitted attempt, including failure/cancellation, retains its cooldown. An addon busy
  response can extend the remaining cooldown by its validated delay.
- Cache successful snapshots and successful empty results only, keyed by
  `(mode, trimmed query)` with ordinal case-sensitive query equality. Keep at most
  128 entries, remove expired entries on access, and evict the oldest if the
  capacity is reached. Under the current
  ten-second admission and 60-second TTL, normal occupancy is much smaller.
  Copy rows on publication so later source changes cannot mutate cached data.
- TTL is at most 60 seconds from capture (and at most 60 seconds after receipt
  for future-skewed clocks). Admission and retained TTL use monotonic elapsed
  time. Cache hits do not access repositories, credentials or RCON, and remain
  available during a different in-flight lookup. Expired/error results never
  serve stale or cross-query rows.
- One linked three-second deadline covers source work, including repository and
  credential reads and the RCON call. Caller cancellation propagates. Failures
  release the in-flight gate without resetting cooldown; no retry or queue is
  added. Operator commands can still arrive after the read-only busy check.

This is a single-API-process pilot. Multiple replicas require shared admission
before enabling search. The addon-wide cooldown remains the independent game
load bound across API restarts. The HTTP limiter is not perimeter DDoS protection.

## Privacy and Web handoff

Use POST body data, never nickname path/query parameters. The API currently has
no HTTP body logging; built-in request diagnostics describe method/path/status,
and HTTP metrics use the route template. This implementation adds no query,
name, nonce, raw frame or credential log/metric fields. Transport errors are
mapped to a fixed unavailable result without logging the exception. Do not enable
body capture in hosting/proxy/APM configuration for this route.

Web must use a server-rendered POST form, escape names, retain the existing
whole-response deadline, and show empty/busy/unavailable explicitly. Do not copy
the nickname into URLs, redirects, referrers, analytics or logs. No automatic
search/retry/polling is planned. The browser must not contact API/RCON directly.
Web implementation and its RU/EN/no-JavaScript/browser acceptance are the next step.

## Verification boundary

Focused tests cover exact names, duplicates, truncation, malformed framing/text,
source binding and fixed command construction, operator-busy handling, cancellation,
timeout, concurrent misses, cache expiry/isolation/empty results, immutable cached
rows, HTTP admission, bounded chunked input, disabled configuration and the exact
public field allowlist. Existing top-ten and UDP transport regressions are included.
Local checks use synthetic data and fake dependencies/local UDP only.

Local verification on 2026-10-14 passed 141 focused tests (search, top-ten
regressions and the existing UDP client). The complete Quality Gate passed:
restore with package audit, format verification, build without warnings/errors,
1106 tests passed and 56 opt-in browser tests skipped, then the vulnerability
report. Documentation validation checked 197 files and 1046 local links. Change
scope resolves to the full CI route, not the addon-only route.

PR/main CI, combined feature release, production installation and public rendering
acceptance are separate evidence. Browser/engine acceptance was not rerun for this
API-only step. No live player search or production data read was performed.
