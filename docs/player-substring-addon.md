# Player lookup addon v2

Local implementation and verification completed on 2026-10-15. Addon version:
`0.26.0`. This implements the addon part of the
[substring contract](player-substring-search-feasibility.md); API/Web integration,
remote CI and production rollout are separate pending steps.

## Command and compatibility

```text
goldsrcops_player_lookup_v2 <16-hex-nonce> <utf8-hex-query>
GSLOOKUP 2 <nonce> <unix-seconds> <count> <has-more>
GSPROW <ordinal> <rank> <kills> <deaths> <actual-name-uppercase-utf8-hex>
GSPEND 2 <nonce> <unix-seconds> <count> <has-more>
```

The new fixed server command searches a literal contiguous substring. After
strict UTF-8 decoding and trimming outer ASCII spaces, the query must contain
at least three Unicode scalar values within the existing 31-byte limit. ASCII
A-Z and Russian U+0410..U+042F/U+0401 use the contract's fixed, length-preserving
lowercase mapping. Other characters stay literal: no normalization, accent
stripping, transliteration, wildcard interpretation or full Unicode case fold.

Names are folded during each scan; there is no second name cache. The same
4096-record index, score/private-key tie order, ten-result limit, duplicate
handling and zero/deleted/missing-name exclusions apply. Every row encodes its
actual cached name with original spelling. The complete ASCII frame stays below
2048 bytes; header/trailer version, nonce, timestamp, count and truncation agree.

Non-success replies use version 2:

```text
GSLOOKUP 2 invalid
GSLOOKUP 2 <nonce> unavailable
GSLOOKUP 2 <nonce> busy <retry-seconds>
```

`goldsrcops_player_lookup` and wire v1 retain exact, case-sensitive matching,
including one- and two-scalar queries and their existing argument-count behavior.
Both versions use one `g_lookup_after` deadline. Valid-nonce attempts consume
admission before query/source validation; busy attempts neither scan nor extend
the deadline. V2 also consumes admission for missing/extra arguments when the
nonce is valid. Invalid nonces are never echoed. An active cooldown takes
precedence over query/source checks. Switching command versions or restoring
source/enable flags cannot bypass admission. Retry seconds remain rounded up.

Both commands use the existing default-off lookup flag and weapon-addon enable
flag. The registered commands remain inside the existing private server/RCON
boundary. Public API, Web, host configuration and enable state were not changed.
No HTTP mode field or fallback behavior is implemented by this addon-only step.

## Data preservation

The product lookup path only reads current in-memory counters and validated
cached names. It does not open/read/write vaults, scan files, refresh the global
top-ten cache, or update preferences. Existing name write/readback paths remain
unchanged; failed readback cannot publish an unconfirmed name. Vault filenames,
schemas and records are unchanged.

The extended [persistent-stats fixture](../tools/smoke/amxx-persistent-stats.sma)
uses real nVault with synthetic data. It checks:

- V1 exact behavior before and after v2; both registered server handlers;
  versioned complete responses, actual distinct names, score and private tie order.
- Russian case pairs including U+0401/U+0451, fixed literal behavior for other
  scripts/accents, four-byte scalars, ASCII trim, maximum bytes and minimum scalars.
- Malformed UTF-8/hex, controls, identifiers, bad nonce/argument list, unavailable
  sources, duplicate/truncated results, missing/invalid/deleted/zero records.
- V1-to-v2 and v2-to-v1 cooldown, failed admission, unchanged deadline on busy,
  fractional wait rounding, expired deadline, source/enable restoration.
- Zero product vault reads/writes and file scans during both modes; unchanged
  top-ten output and no rebuild; successful/equal/failed name readback behavior.
- All 30 seeded statistics entries, 30 name slots (including an absent one), and
  language/weapon, HUD and streak records before/after queries and after closing
  and reopening both native vault handles, preserving values and timestamps.
- All 8192 statistics/name records in the capacity fixture, preserving values and
  timestamps after queries and closing/reopening both vaults.

The fixtures instrument product vault calls and capture formatted output. They
exercise complete response generation and registered server callbacks, not UDP
RCON transport. The capacity probe resets admission only between accepted samples;
its 1000 alternating-version busy attempts retain one active deadline. Expiry
boundary assertions set the fixture deadline explicitly; they are not a wall-clock
measurement of ten seconds. No real player data is used.

## Local evidence

Product and all five existing weapon fixtures compiled with pinned AMXX
1.10.0.5481/ReAPI 5.24.0.300. The game-host package smoke passed with the existing
`-EnablePlayerLookup` switch; no package was uploaded or installed.

The full player-preferences engine suite passed with zero failures and no AMXX
error logs: settings/HUD, RU/EN menu/help, nine loadout combinations, ranks,
persistent statistics, public top ten, standing, profile, streaks, cross-process
and cross-map reloads, both lookup modes and the 4096-record probe.

The local adapter used the pinned game-files image and verified replacement
engine/modules/Steam library described in the
[research environment](player-substring-search-feasibility.md#reproduction-and-evidence-identity).
It ran the existing runtime suite's case sequence under `runuser` with offline
`-insecure` startup, two CPUs, 1 GiB, no network/published ports, read-only inputs
and a disposable writable container. The adapter and detailed logs are retained
with local evidence. No production host or credentials were used.

Final full-suite callback timings, 4096 matching records, maximum counters and
31-byte names, two warmups then 30 samples per mode:

| Probe | Result |
| --- | --- |
| V1 complete response | mean 2.375 ms; maximum 2.934 ms |
| V2 complete response | mean 4.161 ms; maximum 4.890 ms |
| Index initialization | 24.611 ms |
| 1000 mixed-mode busy calls | 0.365 ms total |
| Product vault/file IO during queries | 0 |

A preceding focused run of the same final product/fixture observed a v2 maximum
of 10.210 ms. Both runs passed the 25 ms lookup and 1 second initialization
budgets. These are local fixture-captured callback timings, not production or
network latency guarantees. The existing 512 KiB name payload is unchanged;
there is no additional folded-name cache or measured RSS claim.

| Artifact | SHA-256 |
| --- | --- |
| Product source | `51cd892b67d83280cc868691b5de5120b71623b90e51461c36ac7af4e20434e4` |
| Persistent-stats fixture | `da30f9ff2ab6b97e1932ac2d7fe289cca51f1ba3c77e038f3fe34e4936341432` |
| Compiled product | `99012082d207756ce1183ffb19b3a8e65c33ca6785081cc441099917ed644af7` |
| Local game-host package | `a4f8c89992767cf208948e00103ba17f7ef9eea586f1046e9f194aee813956fc` |

The focused and full engine runs, compilation and package smoke are local evidence.
Remote push/PR/CI, publication, installation, production acceptance, full .NET and
browser checks were not performed for this addon-only step. The next product
slice is API mode validation, v2 parsing and mode-specific cache keys, followed
by Web mode selection. A later release must independently verify mixed versions,
upgrade/rollback preservation and actual RCON transport before enabling the UI.
