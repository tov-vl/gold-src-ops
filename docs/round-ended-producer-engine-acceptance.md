# round.ended Producer Engine Acceptance

Accepted on 2026-11-16 through PR #553 after full PR/main CI and independent
verification of downloaded fixture and engine-evidence artifacts. This slice
records the existing producer contract in executable tests; it changes neither
the producer source/version nor the event schema or human-activity policy.
Production rollout and real-player activity acceptance remain separate.

## Current Contract And Native Coverage

The included producer source registers its real post-call `RG_RoundEnd` and
`RG_CSGameRules_RestartRound` hooks. The fixture invokes `rg_round_end` with
hook triggering enabled and uses `rg_restart_round`; neither production callback
is called directly by the fixture. The final scenario observes 22 round-end
hooks and 12 restart hooks.

Roster enumeration remains the real `get_players(..., "h")`. Three fake clients
represent two ordinary clients and one HLTV proxy flag. One designated client
uses fixture-only human classification for `is_user_bot`; no real player,
Steam identity, network join or HLTV handshake is claimed. The shipped producer
is compiled separately without any substitution.

| Situation | Expected existing behavior |
| --- | --- |
| Empty observable round | No record; the round guard is consumed |
| Clients arriving after that empty callback | No record until a restart |
| Two bots plus HLTV | One record, `players=2`, `bots=2` |
| One simulated human, one bot and HLTV | One record, `players=2`, `bots=1` |
| The simulated human becomes a spectator | Counts remain `2/1`; these are connected-client counts |
| The bot is disconnected through the engine | One record, `players=1`, `bots=0` |
| Last non-HLTV client disconnects | HLTV-only roster creates no record |
| None/commence/restart pseudo-rounds | No record and no consumed observable-round guard |
| Repeated observable callback | At most one successful record in that round |
| Actual round restart | A new record is admitted with a distinct canonical ID |
| Disabled, then enabled in the same round | Disabled callback creates nothing; enabling permits a record |

Bot-only events are intentionally still emitted. `players - bots` is a snapshot
of connected non-bot clients, including spectators; it is not active-player
participation, unique humans, demand or a count of humans who played earlier in
the round. Suppressing bot-only events or changing these meanings needs a
separate contract decision. No 24-hour activity aggregation is introduced.

## Filesystem And Failure Evidence

The same fixture uses a fresh local spool and checks:

- Invalid capacity and a full spool reject intake without consuming the round.
  Existing `.tmp` and unknown files count toward capacity, directories do not.
  Those evidence files remain present after a later successful intake.
- A real directory-permission fault prevents the unprivileged engine user from
  writing. Restoring that fixture directory permits a later callback to succeed.
- A fixture-only rename refusal occurs after the real producer has written,
  flushed and closed its temporary file. The producer removes its own `.tmp`,
  creates no ready record and can publish on a later callback.
- Successful commits use the actual native rename. A nonempty closed temporary
  file exists before handoff; the ready name does not. The final ready file
  exists and its temporary name is gone after handoff.

The runtime exports only the ten canonical ready records named in the fixture
manifest. Before copying each original, it independently checks `0600`, a single
hard link and no symlink, then records its size and SHA-256. Unrecognized and
incomplete backpressure files are excluded from this transfer.
Only the exported synthetic copies use `0644`, so the unprivileged CI runner
can read them for agent acceptance and artifact retention. Their bytes must
match the originals; the spool originals retain `0600`.
This is complete-file process handoff evidence, not a power-loss durability
claim; AMXX still provides no portable filesystem sync primitive here.

## Actual Agent Contract

`game-event-producer-contract.py` verifies the bounded transfer receipt, exact
case counts, IDs, JSON roster values and byte hashes. It then builds the
unchanged agent with NuGet audit and invokes the existing `initialize-queue --fresh`
and `import-spool` commands against a unique temporary spool/SQLite
queue. Delivery is explicitly disabled in that child process.

All ten actual engine records must be imported with no rejection or deferral.
Read-only queue inspection checks event type, map, player/bot counts, pending
status and zero delivery attempts. Five malformed controls are then passed to
the real importer: mismatched file name, unsupported spool version, bots greater
than players, unknown event member and truncated JSON. All five must be
quarantined while the ten accepted events remain unchanged. The temporary
queue/spool is removed after all readers are explicitly closed.

No replacement C# validator is added. Agent and API source are unchanged.

## Runner And CI Integration

The existing pinned engine runner now transfers twelve source/hash-bound
binaries and requires 23 cases / 46 markers. The new case requires all four
`PRODUCER_*_SMOKE` results exactly once with zero failures. Marker deletion,
duplication, failure and changed producer/fixture source reject acceptance.

`--disabled-producer-check` runs the same fixture with its producer disabled.
Integration verification requires the precise missing bot-only record assertion,
failed runner status and successful cleanup. This joins the prior three real
negative runs; a setup or compiler failure cannot replace the assertion.

Full CI invokes the actual agent-contract bridge after engine success and
retains its result/log plus the new negative-run evidence for seven days.
The existing addon-only, documentation-only and stable-promotion paths keep
their boundaries; only full CI builds the agent for this additional check.
No workflow permissive fallback, new credential or published port is added.

To reproduce, compile the existing weapon/map fixtures and additionally run
`amxx-game-event-producer.ps1 -RuntimeFixture -Offline` with the same verified
compiler cache. The ordinary compiler invocation already builds the unchanged
producer binary. Use the existing `engine-ci.py` positive/integration commands,
then run:

```text
python -B tools/smoke/test-game-event-producer-contract.py
python -B tools/smoke/game-event-producer-contract.py --evidence <positive>/evidence --output <fresh-contract-output>
```

See the [shared engine setup](v2.90-engine-ci.md#local-command) and the
[producer protocol](game-events.md#sandbox-amx-mod-xreapi-producer).

## Local Verification And Limits

- All twelve binaries compiled with AMXX 1.10.0.5481 and ReAPI 5.24.0.300.
- Fifteen runner tests and two transfer-evidence tests passed. The latter cover
  six corruptions: changed bytes, an extra file, a missing case, reused identity,
  substituted roster and an escaping file name.
- The full engine regression passed 23 cases / 46 markers in 184.407 seconds,
  with successful cleanup and no nonempty AMXX error log.
- The actual agent imported ten native records and rejected all five malformed
  controls. Its build completed with zero warnings/errors and temporary queue
  cleanup passed.
- Four real negative runs were rejected with their exact assertions and cleanup:
  intentional assertion, missing map provider, disabled spawn protection and
  disabled producer. The same compiled manifest was used throughout.
- The CI routing/retention contract, documentation links and scoped diff were
  checked locally. Full required PR/main CI subsequently passed with independent
  artifact acceptance as recorded below.

No C# was authored, so InspectCode is inapplicable. The unchanged full local
.NET solution gate was not repeated; the agent build/importer was exercised
directly. No production server, client laptop, player data, bot enablement,
Archive setting, plugin version, application image or event schema was changed.

## Hosted Delivery Evidence

[PR #553](https://github.com/tov-vl/gold-src-ops/pull/553) accepted head `68dd34a0796369630af2cc4e04207be95c115bcf`
as main revision `a9d21b88d1a19bef17d5b8d83ec7d103004f667e` after green required
checks and CLEAN/MERGEABLE. No protection bypass or workflow rerun was used.

| Evidence | PR | Post-merge main |
| --- | --- | --- |
| Full CI | [Passed](https://github.com/tov-vl/gold-src-ops/actions/runs/43845365914) | [Passed](https://github.com/tov-vl/gold-src-ops/actions/runs/43847968165) |
| Positive engine suite | 23 cases / 46 markers, 159.367 s | 23 cases / 46 markers, 160.587 s |
| Four precise negative assertions | Rejected | Rejected |
| Twelve compiled payloads and source/toolchain binding | Verified | Verified |
| Actual agent: ten native records / five malformed controls | Accepted / rejected | Accepted / rejected |
| Delivery disabled and owned-container/temporary-queue cleanup | Passed | Passed |

The initial PR run exposed unreadable root-owned copies of synthetic records
([failed CI](https://github.com/tov-vl/gold-src-ops/actions/runs/43843876893)).
A signed correction exports byte-identical diagnostic copies as `0644` while
checking that original spool records remain `0600`. The subsequent complete
PR/main runs listed above accepted that correction. No manual rerun was used.

Both compiled-fixture and engine-evidence ZIPs were independently downloaded.
Payload hashes, pinned toolchain, exact source identities, all 46 markers, all
four precise negative assertions and matching manifest/lock identities passed.
The original native records were checked against their bounded transfer receipts
and the actual agent result from the same run; all ten byte hashes matched.
The successful runs had no nonempty AMXX error log. Engine, negative-producer
and agent-contract evidence is retained by CI for seven days.

Full CI executed the .NET restore/audit, format, build, test, vulnerable package
report, client-policy, browser and container checks. The unchanged full local
.NET gate was not repeated and InspectCode was inapplicable because no C# was
authored. No game-server update, addon publication, application tag, Archive
change, client-laptop action or real-player acceptance occurred in this delivery.
