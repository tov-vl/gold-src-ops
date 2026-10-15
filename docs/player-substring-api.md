# Player search API modes

This local slice implements the API portion of the
[substring contract](player-substring-search-feasibility.md), following
[addon v2](player-substring-addon.md). It does not deploy the addon/API or add
mode selection to the website. The subsequent [Web slice](player-substring-web.md)
adds that selection; the exact-only Web behavior below records the API-stage boundary.

## Behavior

| Request | Semantics |
| --- | --- |
| `{"name":"Ab"}` | Existing exact, case-sensitive search; wire v1 |
| `{"name":"Ab","match":"exact"}` | Same exact search; wire v1 |
| `{"name":"abc","match":"contains"}` | Literal substring with fixed ASCII/Russian case map; wire v2 |
| Explicit null, empty, unknown or non-string `match` | HTTP 400 `invalid`, no source access |

An omitted property uses the request constructor's `exact` default; explicit
JSON null stays null and fails validation. Mode values are case-sensitive and
are not trimmed. Unknown JSON members remain forbidden. Both modes retain the
bounded POST body, no-store response, configured target/credential and existing
limiter. The response fields and status mapping are unchanged.

Application validates strict UTF-8, the 31-byte name bound, outer ASCII-space
trimming, controls and identifier exclusion. Contains additionally counts at least
three Unicode scalars, including four-byte characters as one scalar. The fixed map
is ASCII A-Z and Russian U+0410..U+042F/U+0401; every other character remains
literal. No full Unicode folding, normalization, transliteration or wildcard
expansion is introduced. Ordinal comparison after this map agrees with the addon
for validated strings.

Infrastructure independently validates canonical input before repository/secret
access and selects one of two fixed command names. Contains uses
`goldsrcops_player_lookup_v2`; exact uses `goldsrcops_player_lookup`. Commands
contain only the generated nonce and hex query. Each attempt makes at most one
transport call and never retries with the other version.

The parser requires the version selected by the request for success, busy,
unavailable and invalid responses. It retains complete framing, nonce, freshness,
ASCII/byte bound, count, ordinal, score/rank and ordering checks. Every decoded
result name must be valid and match the requested mode. V2 returns that actual
name with original spelling and retains duplicates. Unknown commands, mixed
versions, unrelated names and partial frames fail closed; the service maps them
to unavailable without publishing or caching partial rows.

## Shared admission and separate results

There is one service instance, one in-flight slot, one cooldown and one bounded
128-entry dictionary across both modes. Cache keys contain the mode and trimmed
query; string equality is ordinal. Different case variants can occupy different
entries even in contains mode. Exact results, including empty results, cannot
satisfy a contains request or vice versa.

The existing 60-second capture-based lifetime, immutable row publication,
expiration/eviction, cancellation and three-second source deadline are retained.
Busy/error/cancelled attempts keep shared admission. Valid cached results remain
available during another lookup, and failed/expired results never serve stale or
cross-mode rows. No additional game requests, cache partition or timer retry is
introduced.

## Compatibility and remaining integration

Old callers omit `match` and retain exact behavior. The shared request DTO now
has an optional mode, so the existing Web client explicitly serializes only its
original `name` field. This keeps the current website compatible with old APIs
that reject extra fields; UI, form processing, response validation and manual
countdown remain exact-only. A transport test verifies that outgoing property set.

Deploy addon v2 before the new API, then the later Web mode-selection slice.
New API plus old addon can still serve exact searches; contains returns unavailable
without fallback. Old API plus an explicit mode rejects the extra field. Roll back
Web before API/addon and retain current vault data. Release/transport acceptance is
separate from the local HTTP and mocked-RCON tests.

## Local verification

Focused checks cover HTTP omission versus explicit null, modes and scalar limits;
fixed ASCII/Russian matching vectors and literal Unicode exceptions; original v2
names/duplicates, corrupted and mismatched frames; fixed command selection and no
fallback; cross-mode admission, case-sensitive keys and independent expiry; and
the unchanged Web JSON request body. Repository/credential/transport boundaries
are mocked; HTTP tests use the in-process application host.

Results on 2026-10-15:

- Focused lookup tests: 139 UnitTests and 41 WebTests passed.
- Audit restore, full formatting verification and full build passed; build had
  zero warnings and errors. The dependency audit reported no known vulnerable
  direct or transitive packages across all 13 projects.
- Full solution test attempts were not green: the first had one failure in
  `ExecuteAsync_throws_timeout_when_first_command_response_does_not_arrive`;
  the second had that failure plus
  `ExecuteAsync_rejects_continuous_response_at_overall_deadline`.
  These existing loopback RCON tests use short wall-clock deadlines. Their source
  and the RCON client were unchanged. Parallel-load sensitivity is suspected,
  not established as the root cause.
- The focused RCON class passed all 11 tests. A subsequent standalone UnitTests
  run passed all 753 tests without rebuilding or changing code. WebTests passed
  398 tests and AlertReceiver passed 54 in both full-solution attempts.
- The 61 opt-in browser tests were skipped. Browser/container/real-addon
  integration was not rerun for this API slice. A clean standard CI test run
  remains required; the isolated passes do not replace the failed local gate.

The subsequent [Web-stage verification](player-substring-web.md#verification)
passed the full local test command without changes to those RCON tests. The
failure history above remains relevant; its root cause is still unconfirmed.

No remote push, PR, CI, tag, publication, deployment, production query or live
RCON operation is part of this local slice. No database migration or configuration
change is needed.
