# Player lookup addon foundation

Addon 0.25.0 implements the first step of
[player search outside the top ten](player-search-feasibility.md). This is an
addon-only implementation boundary within the R2 addon/API/Web feature.
The combined release is R3 because it also extends recovery tooling.
API/Web, HTTP admission/cache and the user-facing form are not implemented here.
No production installation or application candidate is part of this step.

## Command and protocol

The server-only command is:

```text
goldsrcops_player_lookup <nonce> <utf8-hex-name>
```

`nonce` is exactly 16 ASCII hex characters, preserved verbatim for correlation.
The query is 2-62 hex characters, decoding to 1-31 bytes of strict UTF-8. Embedded
NUL, overlong encodings, isolated continuation bytes, surrogates, values above
U+10FFFF, C0/C1 controls and case-insensitive `STEAM_` substrings are rejected.
Leading/trailing ASCII spaces are trimmed; other Unicode whitespace is literal.
Matching is exact and case-sensitive with no normalization or transliteration.

Both `goldsrcops_weapons_enabled` and `goldsrcops_player_lookup_enabled` must be
nonzero. The lookup switch defaults to zero. Ordinary package configuration
remains unchanged; the builder's explicit `-EnablePlayerLookup` option is valid
only with `-Target game-host-addon` and adds the persistent lookup command.
Sandbox activation is rejected. The [v2.76 recovery extension](v2.76-readiness.md)
accepts only this exact configuration transition and its reverse, preserving
receipts, backup baseline and current player data. Invalid argument count/nonce
returns the fixed invalid response without echoing input. A valid nonce consumes the shared ten-second
cooldown before query/source validation, including on invalid/unavailable
results. An active cooldown returns busy before scanning. It is independent of
API process restarts and enable toggles; it resets with the game map/plugin.

Successful ASCII response, including a valid empty result:

```text
GSLOOKUP 1 <nonce> <unix-seconds> <count> <has-more>
GSPROW <ordinal> <rank> <kills> <deaths> <uppercase-utf8-hex-name>
...
GSPEND 1 <nonce> <unix-seconds> <count> <has-more>
```

There are exactly `count` rows (0-10). Ordinals are contiguous 1-based result
positions, not global ranking or persistent identity. `has-more` is 0 or 1.
Rows use existing kills-descending/deaths-ascending/private-key tie ordering;
identical public rows are not deduplicated. Counters retain the existing
0-1,000,000,000 limit, and rank retains the five existing thresholds. The
header/trailer metadata must agree. Ten maximum-width rows and the two framing
lines fit within 2048 ASCII bytes, including LF separators. The future API must
validate completeness and this bound, never accept a partial result.

Non-success replies are exactly one line:

```text
GSLOOKUP 1 invalid
GSLOOKUP 1 <nonce> unavailable
GSLOOKUP 1 <nonce> busy <retry-seconds>
```

`retry-seconds` is rounded up and between 1 and 10. Non-success responses have no
rows. Invalid input is not echoed. Future HTTP code must generate nonces itself,
verify exact correlation, and avoid logging query/frame content. The nonce is a
correlation field, not authentication; the existing private RCON boundary applies.

For the additive substring command in addon 0.26.0, see
[lookup v2](player-substring-addon.md). The v1 contract below remains supported.

## State and compatibility

Each existing bounded index record gains a 32-cell validated name cache. Names
are loaded from the preferences vault during ordinary index initialization,
without a new file/schema or startup write. The existing successful name
write/readback (or equal-value readback) refreshes the cache. Failed readback does
not publish an unconfirmed name. Missing/invalid names remain unsearchable;
a display fallback is never synthesized into the search index. An unavailable
stats/preferences vault or invalid index returns unavailable, not an empty match.

A lookup scans at most 4096 records in one engine callback and keeps at most ten
matches. It reads current in-memory saved counters and cached names from that
callback, without yielding. It does not refresh or mutate the five-second
top-ten cache, enumerate files, read vaults or write player data. Only the lookup
cooldown changes. Capture time means response snapshot time, not last combat or
last disk flush. Zero-kill/zero-death and deleted records remain excluded.

The existing `goldsrcops_leaderboard_snapshot` command, MOTD/chat/menu output,
scoring, rank thresholds, preferences and vault formats are unchanged. Old API
versions ignore the new command; a future API must treat an old addon or disabled
lookup as unavailable. No public player key or stable profile ID is introduced.
A future rollout/rollback must retain current vault data and use the existing
claimed addon workflow and matching backup baseline.

## Verification boundary

The existing persistent-stats fixture includes `goldsrcops_player_lookup_smoke`
and `goldsrcops_player_lookup_load`. They are included in
[`game-player-preferences-runtime.sh`](../tools/smoke/game-player-preferences-runtime.sh),
which now prints engine error logs and recognizes the load-probe marker.
The fixture uses real nVault with synthetic records in a disposable engine;
inputs are read-only mounts, network is disabled, and no ports are published.

The functional scenarios cover offline players below the top ten, duplicate
and identical rows, truncation, empty matches, 0/0 exclusion, missing names,
Cyrillic, four-byte UTF-8, maximum name size, markup, malformed UTF-8/hex,
identifier-like names, exact case, spaces, command arguments, enable/source
failures, cooldown persistence and successful/equal/failed name readback.
The existing top-ten row output and cache counters must remain unchanged.

The load probe uses 4096 matching saved records, maximum counters and 31-byte
names. It takes 30 complete response-generation samples and 1000 cooldown-hit
calls, then verifies all 4096 statistics and name values/timestamps unchanged.
It checks zero product vault/file IO during queries. Timing uses the fixture's
`EngFunc_Time`, backed by [ReHLDS `PF_Time` / `Sys_FloatTime`](https://github.com/rehlds/ReHLDS/blob/master/rehlds/engine/pr_cmds.cpp),
not game-tick time. Pilot fixture budgets are below 25 ms per sampled lookup and
below 1 second for index initialization; these are engineering checks on the
constrained local runner, not a live-service latency guarantee.

The name-cache payload adds at most `4096 * 32 * 4 = 524288` bytes (512 KiB).
This is the bounded record payload increase, not a measured process RSS delta;
allocator overhead, existing index data and fixture buffers are separate.
Full .NET/HTTP/browser acceptance and candidate timing belong to the later
API/Web integration. Game-client visual and two-player acceptance stay separate.

## Local results - 2026-10-14

Product source SHA-256:
`7a062ec4b2c2b4ba95e7e8ebfaad560c8ce8ed7b5bab7c92428ea335ed556bcd`.
Pinned AMXX 1.10.0.5481/ReAPI 5.24.0.300 compiled the product and all five
existing weapon fixtures successfully. Local game-host package validation passed;
the package was not uploaded or installed.

The complete player-preferences engine suite passed, including settings/HUD,
menu/help, nine loadouts, ranks, persistent statistics, public top ten, standing,
profile, streaks, cross-process/map reloads and both lookup scenarios. All markers
reported zero failures and no AMXX error log was present. The disposable container
used two CPUs, a 1 GiB memory limit and no network or published ports. Engine image:
`sha256:e37fb83ba586490df67e2989935f42448dbd1aca429b699dc99efcbdc37a64a2`.

| Probe | Observed value |
| --- | --- |
| Complete lookup, 4096 matching records, maximum counters/names, 30 samples | mean 1.708 ms; maximum 1.810 ms |
| Closed-vault index initialization including name cache and existing top-ten rebuild | 18.258 ms |
| 1000 calls during one active cooldown, no scans | 0.286 ms total |
| Added bounded name payload | 512 KiB; not process RSS |
| Product vault reads/writes or file scans during the measured queries | 0 |

These are local callback timings with fixture-captured output, not RCON network
latency or production tick measurements. Initialization excludes synthetic seed
creation and file close. The fixture resets the cooldown between the 30 accepted
samples only; the 1000-hit scenario leaves it active. All 8192 stored stats/name
values and timestamps were compared after the probe. No live player data was used.

Required PR/main CI is independent of these local checks and follows the
addon-only route. The full .NET/browser suite, application candidate publication,
production installation and public-search acceptance were not run for this step.
