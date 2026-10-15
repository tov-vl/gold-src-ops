# Partial player-name search - contract and local feasibility

Status: local research completed on 2026-10-15. Product implementation and release
are not part of this step. Inspected baseline: `5f520a5a46e046c2a8212f7893adc033c77419e5`.
The recommendation is to implement a separate, explicit substring mode over the
existing in-memory index, folding names during each request. Keep exact search.

## Findings from current source

- [The addon](../samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma)
  already caches validated names for up to 4096 saved records. `FindSavedPlayers`
  scans that index without reading vaults and uses the existing score/tie order.
- `EmitPlayerLookup` encodes the query once and uses it as the name in every row.
  That is correct only for exact matching. Substring results must encode each
  matched record's actual cached name.
- [The parser](../src/GoldSrcOps.Infrastructure/Monitoring/PublicPlayerSearchProtocol.cs)
  requires every returned name to equal the query. It must retain that check for
  version 1 and use the new matching rule only for version 2.
- [The request](../src/GoldSrcOps.Contracts/Monitoring/PublicPlayerSearchResponse.cs)
  currently contains only `Name` and rejects unknown JSON members. An old API
  will reject a new `match` member; it will not silently execute another mode.
- [The service](../src/GoldSrcOps.Application/Monitoring/PublicPlayerSearchService.cs)
  owns one in-flight request, a shared ten-second cooldown and a 128-entry cache.
  Both modes must share admission while using separate cache keys.
- [The Web form](../src/GoldSrcOps.Web/Components/Shared/PlayerSearch.razor) uses
  an ordinary CSRF-protected POST. The current countdown remains a manual retry.

No production player data was read, exported or seeded. The research used only
synthetic records in a disposable local engine.

## Recommended user contract

Offer RU/EN choices "Part of name" and "Exact name", with substring selected on
new forms. Exact mode remains available for names shorter than three characters
and for the previous case-sensitive semantics. Preserve the submitted mode and
text across busy responses, countdown completion and leaderboard refresh. Do not
silently switch modes or issue a second lookup after an empty result.

| Rule | Exact mode | Substring mode |
| --- | --- | --- |
| Input after trimming outer ASCII spaces | 1-31 strict UTF-8 bytes | 3 or more Unicode scalar values, at most 31 strict UTF-8 bytes |
| Match | Whole saved name, case-sensitive | Contiguous literal substring after the fixed mapping below |
| Case mapping | None | ASCII A-Z to a-z; Russian U+0410..U+042F to U+0430..U+044F; U+0401 to U+0451 |
| Other characters | Literal | Literal, including other alphabets, accents, punctuation and spaces |
| Invalid input | Existing control/UTF-8/identifier checks | Same checks plus the three-scalar minimum |

Web/API trim before checking the byte bound and send only canonical trimmed text.
Keep the existing bounded raw-input guard. The addon independently validates the
hex/UTF-8 boundary and counts scalars after trimming. Count scalars, not UTF-16
code units or bytes; an emoji is one scalar. This is not a grapheme-count promise.
C0/C1 controls, malformed encodings and case-insensitive `STEAM_` fragments remain
invalid. Three punctuation characters are allowed as literal input; they are not
wildcards, and the existing shared cooldown still applies.

No transliteration, wildcard syntax, regex, accent stripping, NFC/NFKC, full
Unicode case expansion, or Latin/Cyrillic lookalike equivalence. In particular,
U+0451 stays distinct from U+0435. The UI should explicitly say that Russian and
English letter case is ignored, rather than promising every alphabet's case rules.

AMXX [containi](https://www.amxmodx.org/api/string/containi) supports UTF-8 comparison,
but that alone does not establish parity with .NET's case rules. Use the same small,
length-preserving mapping in addon and Application, then an ordinal byte/string
substring comparison. Share fixed conformance vectors between their tests.

Return up to ten eligible records, ordered by existing kills descending, deaths
ascending and the private key tie rule. Do not prioritize exact matches or collapse
identical names/rows. Preserve original name spelling in results. `HasMore` means
additional matches were omitted and the user should make the query more specific;
there is no total count or pagination. Missing/invalid cached names, deleted
records and 0/0 records remain excluded. Explain that names are last recorded,
not unique identities or evidence of online status.

## Compatibility and load boundary

Keep `goldsrcops_player_lookup`, `GSLOOKUP 1` and the existing top-ten export intact.
Propose a new fixed server command:

```text
goldsrcops_player_lookup_v2 <nonce> <utf8-hex-query>
GSLOOKUP 2 <nonce> <unix-seconds> <count> <has-more>
GSPROW <ordinal> <rank> <kills> <deaths> <actual-name-utf8-hex>
GSPEND 2 <nonce> <unix-seconds> <count> <has-more>
```

This command means only the specified substring mode. Keep the 16-hex nonce,
2-62-hex transport bound, strict UTF-8, 2048-byte complete frame, and versioned
invalid/unavailable/busy replies. Both commands must consume the same
`g_lookup_after` slot, including failed valid-nonce attempts. Never introduce a
second cooldown or automatic fallback to the other command. Existing enable flags
and vault formats can remain unchanged; no new persistent name index is needed.

Extend the HTTP request with an optional `match`: omission means `exact`, explicit
values are `exact` and `contains`; null, empty and unknown values are invalid.
The source selects the fixed command internally, never from arbitrary caller text.
Validate returned names using the selected mode and retain nonce, capture-time,
score/rank, ordering, row-count and complete-trailer checks. Keep the public
response shape, errors, no-store headers and bounded retry delay.

Use `(mode, trimmed query)` as the cache key with ordinal query equality. Do not
reuse an exact result for substring search. Retain one shared in-flight slot,
ten-second admission, 128 total cache entries, 60-second freshness, three-second
transport deadline, no retries and the existing HTTP limiter. Case variants may
occupy separate entries; bounded cache behavior is simpler than changing cache
canonicalization in this slice.

The Web BFF continues to call the API server-side. No requests on keypress or timer
expiry, query in URL/referrer, browser storage, public player IDs, query/name log
fields, metric labels or new database storage. Browser acceptance must explicitly
select exact mode for a saved nickname shorter than three scalars and cover both
modes. No-JavaScript submission and the existing countdown remain required.

Roll out addon before API before Web; roll back Web before API/addon. Old clients
omit `match` and retain exact behavior. New API with old addon returns unavailable
for contains without fallback; old API rejects the extra field. New Web must show
that failure clearly rather than displaying stale results or calling it empty.
The later addon/API/Web implementation is expected to follow the existing R2
search precedent and claimed addon upgrade/rollback process; classify the frozen
release again if recovery/configuration scope expands. This research does not
publish a command, enable a feature or authorize a production rollout.

## Measured prototype

The opt-in [Pawn fixture](../tools/research/player-substring-search.sma) includes
the unchanged product index/comparator and adds research-only matchers. The
[runner](../tools/research/player-substring-search-runtime.sh) uses real ReHLDS,
AMXX and ReAPI, no network or published ports, read-only inputs, two CPUs, 1 GiB
and a 90-second outer deadline. It creates synthetic records directly in the
in-memory index: this does not prove vault initialization or upgrade preservation.

Each case uses 4096 maximum-counter records, maximum 31-byte names and reverse
private-key insertion order. Two warmups precede 30 measured samples. The timer is
`EngFunc_Time`; each sample includes matching, bounded top-ten selection and one
common ASCII response serializer. Mode 0 uses existing exact selection; mode 1
folds each name per lookup; mode 2 uses an extra pre-folded name cache. Thus the
exact baseline is a controlled algorithm comparison, not the released emitter's
end-to-end latency. RCON, HTTP, CSRF and browser rendering are outside these timings.

Final matrix, milliseconds:

| Case | Per-request folding mean / max | Cached folding mean / max |
| --- | --- | --- |
| All ASCII names match | 2.149 / 2.512 | 0.667 / 0.851 |
| All Russian names match | 1.787 / 2.696 | 0.704 / 0.813 |
| No match | 1.938 / 2.087 | 0.389 / 0.682 |
| Match at the end of a maximum-length name | 2.774 / 3.355 | 0.858 / 1.173 |
| Long query differs at its last character | 2.168 / 2.418 | 0.451 / 0.541 |

Exact selection with the common serializer: mean 0.655 ms, maximum 2.657 ms.
The initial exploratory pass observed a higher per-request-folding maximum of
6.605 ms; local scheduling varies. Both passes stayed below the existing 25 ms
pilot budget. These are local measurements, not a production tick-time guarantee.
The implementation must repeat the complete-command engine gate before rollout.

The ten-row frame was 1047 bytes and the empty frame 84 bytes. Cached folding adds
`4096 * 32 * 4 = 524288` bytes of payload, excluding allocator/process overhead;
final cache-construction samples ranged from 1.247 to 2.425 ms. No RSS claim is made.
**Choose per-request folding for the first implementation:** it meets the measured
budget and avoids another 512 KiB cache and its update/readback invalidation rules.
Keep the faster cache as a measured alternative if later evidence requires it.

The final run passed with zero fixture failures. It checks all 32 Russian case
pairs plus U+0401/U+0451, ASCII/mixed-script behavior, literal wildcard characters,
no transliteration/normalization, invalid UTF-8/control/identifier input, byte and
scalar limits, match truncation, preserved source records, score/private tie order,
0/0 exclusion, lookup outside the top ten and different actual names in the frame.
The prototype paths have no vault/file calls during matching. Full vault IO and
values/timestamps verification belongs to integration with the existing fixture.

## Reproduction and evidence identity

Use the pinned compiler setup from `tools/smoke/amxx-game-event-producer.ps1` in an
isolated cache, then compile `tools/research/player-substring-search.sma` with the
AMXX, ReAPI and `samples/amxmodx-weapon-selection` include directories. Name the
output `output/player_substring_research.amxx` under the input directory.

Provide the seven assets checked by the runner under `assets/`; extract the
verified ReHLDS, ReGameDLL, Metamod and ReAPI ZIP files into matching directories
under `extracted/`. Reuse the exact archive hashes checked by the runner and
verify them before extraction. Mount the repository at `/repo` and this prepared
input directory at `/inputs`, both read-only. Run `timeout 90 bash
/repo/tools/research/player-substring-search-runtime.sh` with Docker `--network
none --cpus 2 --memory 1g`, no published ports and the image digest below.

| Input | Identity |
| --- | --- |
| Product source SHA-256 | `7a062ec4b2c2b4ba95e7e8ebfaad560c8ce8ed7b5bab7c92428ea335ed556bcd` |
| Prototype source SHA-256 | `b1e315635fe5c919b1a1678a79850f441677e43e9aa63a6f4bd9f7fb61fc32b2` |
| Runner SHA-256 | `4b734b4d55e759b49d47eeb060e45ec633fd7a5f5892786038e34d1810f22664` |
| Compiled prototype SHA-256 | `184503ee1cd9b723d44adb068d0f3d5c71c2cf362347cb2c3966ac8119ed6ffc` |
| Base game-files image | `archont94/counter-strike1.6@sha256:2294ee98981ef1fe4443d9c6a802c74fc9639164ed7a22e294256afb46c32bdb` |
| Compiler / runtime | AMXX 1.10.0.5481, ReAPI 5.24.0.300, ReHLDS 3.15.0.896, ReGameDLL 5.30.0.814, Metamod 1.3.0.149 |

The previous cached engine image was absent on this machine. Anonymous SteamCMD
app-90 installation failed before creating a usable server. The base game-files
image was therefore pinned above; its engine/modules were replaced from the
verified archives. Its old Steam client failed initialization before any tests.
The successful run uses official SteamCMD client version 1791918946's Linux32
`steamclient.so`, SHA-256
`6d8e7e938ff44527a78e2550525c16a9f4605a88d5b20f4466aff311cc00a103`, retained in
the local research inputs, and offline `-insecure` startup. These setup failures
are not algorithm measurements. No production host or credentials were used.

## Next implementation slice

1. Add the version-2 addon command and per-request matcher. Reuse version-1 nonce,
   input, enable-state and shared-cooldown guards; encode each actual result name.
2. Extend the real nVault fixture for both commands, mixed-mode cooldown, same-name
   records, missing/deleted names, readback failures and unchanged data/timestamps.
   Repeat the 4096-record complete-response budget and old top-ten/exact regressions.
3. Add HTTP mode validation, mode-specific parser and cache keys, then Web RU/EN
   mode selection and hints. Add focused shared-vector and mixed-version checks.
4. Run required CI and prepare the separately approved addon/API/Web release.

The current research changes no product source, packages, runtime flags, CI
workflow or deployed service. Compilation, the isolated engine matrix and
changed-documentation validation are local evidence. Full .NET/browser CI,
network transport, production acceptance, game-client visuals and two-player
acceptance were not run for this research step.
