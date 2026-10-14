# Player search outside the top ten - feasibility and first slice

Status: addon foundation implemented and locally verified on 2026-10-14;
API/Web integration and release pending. See [the wire contract and engine
results](player-search-addon.md).
Inspected source: `5dec506a24b98f1b2cb18eaf2dec2a4bbcef847f`.
The original feasibility record was D0. The complete implementation spans addon, API and Web and is
R2, following the existing public leaderboard precedent.

## What is available today

| Boundary | Verified source | Consequence |
| --- | --- | --- |
| Game storage | [Weapon addon](../samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma), `LoadLeaderScores`, `UpdateLeaderScore`, `LEADER_INDEX_LIMIT` | Saved kills/deaths exist for offline players; the in-memory candidate index is capped at 4096 keys. |
| Names | Same addon, `RememberLeaderName` and `RefreshSavedLeaders`; [name contract](v2.52-player-leaderboard.md) | Names are saved in the existing preferences vault at most once per connection. They are neither unique nor necessarily the current nickname. Only ten names are currently cached for display. |
| Transport | `OnLeaderboardSnapshot`; [protocol parser](../src/GoldSrcOps.Infrastructure/Monitoring/PublicLeaderboardProtocol.cs) | The existing frame contains at most ten rows, no private player key, and at most 2048 characters. It cannot answer a search outside the top ten. |
| API | [poller](../src/GoldSrcOps.Infrastructure/Monitoring/PublicLeaderboardPoller.cs), [store](../src/GoldSrcOps.Application/Monitoring/PublicLeaderboardStore.cs), [contract](../src/GoldSrcOps.Contracts/Monitoring/PublicLeaderboardResponse.cs) | One minute polling and one in-memory top-ten snapshot; no full player directory or durable player profile. |
| Event database | [event entity](../src/GoldSrcOps.Domain/GameEvents/GameEventInboxEntry.cs), [history projection](../src/GoldSrcOps.Application/GameEvents/GameEventHistoryDto.cs) | Map/population events contain neither player identities nor individual kills/deaths. They cannot reconstruct lifetime player statistics. |

The current [v2.75 display](v2.75-leaderboard-rank-progress.md) derives progress
from rank and kills in one top-ten snapshot. Extending just that Web component
would search only ten players and would not satisfy the intended feature.
No production player records were queried or exported for this assessment.

## Selected first user-visible slice

Add an anonymous RU/EN search form to `/leaderboard`, with server-rendered
results and normal form submission working without JavaScript. Search the
**exact saved nickname**, including case; trim surrounding ASCII spaces, but do
not invent transliteration, case folding or Unicode normalization in one layer
only. Explain the exact-match and last-recorded-name semantics beside the form.

Accept 1-31 UTF-8 bytes after trimming. Reject malformed text, control characters,
empty input, and names containing `STEAM_` case-insensitively. The existing name
cleaning and strict UTF-8 boundary must be shared by search and export. Names
missing from storage, invalid names and identifier-like names are not searchable;
a display fallback such as "Player" is not a stored name to match.

Return up to ten eligible saved records with the same name, ordered by the
existing kills-descending/deaths-ascending/private-key tie rule. Do not collapse
distinct records with identical statistics. Display nickname, kills, deaths,
rank and kills remaining to the next rank; highest rank has an explicit label.
Use `HasMore` when more than ten matches exist. Do not claim a unique identity,
account ownership or the player's current online state.

The first slice has no persistent profile URL, public player identifier, login
linkage, fuzzy/substring search, pagination, global standing or match history.
Results are inline statistics, not an authenticated "my profile". Zero-kill,
zero-death records remain excluded consistently with the current ranking. Empty
results mean no eligible saved match, not proof that the person never played.

## Proposed implementation boundary

Keep `goldsrcops_leaderboard_snapshot` and `/api/public/leaderboard` unchanged.
Add a separate read-only server command for exact-name lookup over the existing
bounded index. Cache validated names for all indexed records during the normal
load path and update them only after the existing successful name write/readback.
No disk enumeration, vault lookup loop or storage write occurs per search.
An unavailable preferences vault must not masquerade as an empty search result.

The query argument is generated server-side from validated UTF-8 as at most
62 hexadecimal characters. The command verb is fixed; no raw nickname, arbitrary
command, host, port, secret or credential reference comes from the caller.
The addon independently validates argument count, hex, decoded byte length and
text. Use a separate versioned frame with a query correlation value, capture time,
row count, truncation flag, bounded rows and matching trailer. Freeze the exact
wire grammar and invalid/busy/unavailable responses with parser fixtures during
implementation. Preserve the existing transport byte/datagram limits and impose
a 2048-character frame cap for this command as well.

The API owns a separate lookup use case/port and bounded cache; Infrastructure
owns the fixed RCON transport and parser. Use the existing configured public
server and its existing secret reference. Search remains disabled by default.
Skip game access when the operator queue is busy, following the existing poller
pattern; the read-only race with a newly queued command is not a write retry.
Do not enqueue search in the operator command history.

The public API response contains only state, capture time, `HasMore`, and safe
rows. Browser requests go to Web; Web calls API server-side under its existing
whole-response time budget. Names, queries, raw RCON frames and private keys must
not enter logs or metric labels. Review URL/query logging and referrer behavior
before selecting the form method and HTTP route shape; form text is untrusted
and SSR must escape it.

## Proposed load and freshness limits

- One in-flight lookup and at most one new lookup per ten seconds per configured
  server, shared across all anonymous callers, with no waiting queue. An addon
  cooldown enforces the bound even across API restarts; do not reset it on error.
- Cache at most 128 exact queries, including successful empty results, for 60
  seconds using bounded eviction. Cache hits do not contact the game. Different
  case-sensitive queries remain distinct keys. Concurrent cache misses do not
  launch duplicate work. Add a bounded HTTP admission limiter before expensive
  processing; never trust an arbitrary forwarded address as client identity.
- RCON has a three-second deadline, linked cancellation and no immediate retries.
  Throttled requests receive a bounded retry delay. Web displays busy/unavailable
  explicitly and never automatically polls, retries or searches on each keypress.
- Each result batch uses one frozen score/name view and one capture time. Reject
  partial frames, correlation mismatch, invalid ranks/counters and malformed UTF-8.
- Serve successful cached results for at most 60 seconds. After expiry, a failed
  lookup yields unavailable rather than old or cross-query rows. Capture time
  describes the lookup snapshot, not the last match or last durable disk write.
- The single-API-process pilot and addon-wide cooldown bound transport load.
  Multiple API replicas require a shared admission strategy before enabling the
  feature; this proposal does not claim a distributed rate limiter.

These are proposed acceptance limits, not measured capacity. A synthetic engine
probe at 4096 records must report lookup duration, startup cost and memory growth
from the full name cache. If that probe shows an unacceptable gameplay-thread
pause, revise the design before a production rollout; do not silently raise limits.

## Alternatives considered

| Option | Decision |
| --- | --- |
| Filter the current top ten in Web | Reject: cannot find players outside the top ten. |
| Raise the existing top-ten limit | Reject: changes the established frame/UI contract and exports a directory on every poll. |
| Periodically transfer a complete directory | Defer: useful for substring search, but needs bounded paging, coherent multi-page snapshots and a larger data-distribution boundary. |
| Search vault files from the control plane | Reject: couples public reads to private storage files and recovery mechanics. |
| Reconstruct stats from event history | Reject: the required individual events and identities are absent. |
| Exact-name read-only lookup | Select for the first slice, subject to the load probe and transport/security checks above. |

## Evidence and delivery plan

1. Implement the addon lookup and synthetic engine fixture first. Cover an
   offline player below position ten, duplicate and identical names, more than
   ten matches, missing names, 0/0 exclusion, Cyrillic and maximum UTF-8 names,
   control/identifier-like names, malformed arguments and unavailable stores.
   Verify unchanged vault values/timestamps and unchanged existing top-ten output.
   Measure 4096-record work and repeated cooldown hits without production data.
2. Implement API port/parser/cache/admission. Test fixed command construction,
   server/credential binding, timeouts, cancellation, operator-busy handling,
   strict framing, query isolation, cache eviction, empty-result caching,
   concurrency, rate limits, and public field allowlisting. Do not add database
   schema or derive a public ID from SteamID.
3. Implement the inline Web search and RU/EN copy. Test exact-name semantics,
   duplicates/truncation, empty/busy/unavailable states, progress from the same
   result, SSR escaping, mobile/keyboard/no-JavaScript behavior and absence of
   browser-to-API/RCON access. Retain existing leaderboard refresh behavior.
4. Run focused engine/.NET/browser checks and required full PR CI for the combined
   change. Publish immutable addon/API/Web artifacts only after these pass.
   Measure candidate timing against the recorded [baseline](ci-release-timing-2026-10.md)
   on the next real candidate, not on this documentation PR.
5. Freeze the R2 release bundle and previous artifact identities. Upgrade addon
   only after a fresh empty-server check, using the claimed addon workflow and
   matching backup baseline; deploy only affected API/Web digests. Independently
   verify the read-only lookup and public rendering without creating fake live
   players. Synthetic cases prove below-top-ten behavior if the live population
   cannot exercise it. Record that production evidence limit explicitly.
6. Old addon/new API returns unavailable; old API/Web remain unaffected by the
   new command; new Web with a missing endpoint shows unavailable. Rollback uses
   the previous addon/API/Web artifacts and corresponding baseline while retaining
   current player data. No migration, data restore or new credential is planned.

Item 1, the bounded addon lookup and engine probe, is implemented and locally
verified. The next step is item 2, API transport/parser/cache/admission. No game menu/MOTD change is needed. Deferred game-client visual acceptance,
two-player testing and the first successful weekly backup remain independent.
The addon implementation does not yet provide a deployed or accepted public
search feature; the website remains unchanged.
