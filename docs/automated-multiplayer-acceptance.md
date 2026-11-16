# Automated Multiplayer Technical Acceptance

Accepted on 2026-11-16 through PR #551 after full required PR/main CI and
independent verification of both downloaded fixture and engine-evidence artifacts.
This closes isolated technical acceptance. The human gameplay pilot remains
separate.

## Coverage Audit

| Behavior | Existing evidence | Remaining technical gap addressed here |
| --- | --- | --- |
| Enemy damage/death, teamkill, suicide, world death and excluded participants | `amxx-map-stats.sma`: native damage/death messages, six synthetic clients | Protected hits and friendly-fire-disabled interactions under the managed policy |
| Healing, reserve ammo, unchanged clips/active weapon and streaks | Existing combat fixture and [required combat smoke](v2.92-combat-engine-ci.md) | Reward/accounting continuity across a native automatic respawn |
| Nine loadout combinations and team changes | `amxx-spawn-loadout.sma` | Three concurrent clients with different choices; another client's respawn/reset must not replace survivor state |
| Respawn/protection settings and translated guidance | Managed profile checks and [respawn rules](v2.99-respawn-rules.md) | Actual timed immunity, attack cancellation and native delayed/disabled respawn |
| Reconnect, persistent preferences/stats and map reload | Existing preference and persistent-stat seed/reload fixtures | Connection-local reset isolation only; no duplicate vault-recovery suite |
| Map menu, voting, cooldowns and provider/fallback behavior | Existing map fixture and [map engine smoke](v2.91-map-engine-ci.md) | No new map or voting behavior in this slice |
| RU/EN client rendering | Separate [client acceptance](client-guns-acceptance.md) | Not rerun by this headless engine suite |

## New Native Scenario

`goldsrcops_multiplayer_smoke` reuses the compiled spawn fixture in a fresh
three-client process. The shipped weapon addon is still compiled independently;
its source and version are unchanged. Only these synthetic fixture identities
bypass the addon's normal bot exclusion.

The runtime copies the repository's actual `fast-reentry-loadout-v1.cfg`, and the
scenario checks its one-second forced respawn, two-second immunity, visual
protection, attack-only cancellation and disabled friendly fire. Round freezes
and balancing are disabled only for deterministic fixture placement; the shared
runner retains infinite-round isolation. Round objectives and real map flow are
not accepted by this setup.

The scenario checks:

- Protected native enemy damage changes neither HP nor kill/death counters.
- Movement keeps protection; primary and secondary attack remove it through
  native PreThink, including the protection-effects state.
- Timed expiry uses advancing engine time, then permits native damage.
- Friendly-fire-disabled teammate damage leaves HP and points unchanged.
- A real enemy death grants one reward and one kill/death event. The native
  scheduler records a future respawn deadline; the victim returns within a
  bounded interval with fresh protection and its own selected loadout.
- A survivor's active item/loadout and a third client's choices remain intact.
- Disabling forced respawn leaves the next victim dead without pending respawn.
- Explicit disconnect/connect callbacks reset only the selected connection's
  choices. This checkpoint does not claim a real network reconnect or Steam auth.

GameDLL PreThink is pumped for synthetic input/dead-client progress. No test
writes the protection expiry or native respawn deadline, removes protection
directly, or explicitly respawns the victim after the tested death. The
[pinned GameDLL implementation](https://github.com/rehlds/ReGameDLL_CS/blob/5.30.0.814/regamedll/dlls/player.cpp)
owns the damage, immunity and scheduling behavior under test.

The final `MULTIPLAYER_SMOKE` marker is emitted only after the asynchronous
checkpoints. `SPAWN_PROTECTION_SMOKE`, `AUTOMATIC_RESPAWN_SMOKE` and
`MULTIPLAYER_ISOLATION_SMOKE` are also mandatory exactly once with zero failures.
The runner rejects missing, duplicated or failed checkpoints even if the final
marker looks successful. The include and managed configuration are bound into
the compiled-fixture source manifest.

## Negative Evidence And Isolation

The `--missing-spawn-protection-check` mode runs the same fixture with immunity
deliberately disabled after loading the policy. Integration verification must
observe the precise native protected-damage assertion, a failed runner result
and successful cleanup; setup/compiler failures cannot substitute for it.
The existing deliberate engine assertion and missing map-provider negative
checks remain in the same integration command.

All runtime processes use the existing pinned ReHLDS/ReGameDLL/AMXX/ReAPI inputs,
network-disabled disposable container, no published ports, read-only source and
compiled inputs, unprivileged engine user, resource limits and bounded deadlines.
Only owned containers are removed. No live server, player data, account, token,
Archive process or Steam login is involved.

Use the [existing local compilation and runner commands](v2.90-engine-ci.md#local-command),
including the map/combat fixtures described in the later records. No additional
binary or compiler switch is needed. Use fresh output directories for every
invocation; retain positive and all three negative results separately.

## Verification And Claim Limits

Local verification passed:

- All ten fixture/product binaries compiled with pinned AMXX 1.10.0.5481 and
  ReAPI 5.24.0.300, including the changed spawn fixture with no compiler warnings.
- Fourteen runner regressions passed, including removal/failure/duplication of
  each multiplayer checkpoint and rejection of old compiled manifests after a
  real change to either the copied include or managed policy.
- The focused native scenario passed in 37.281 seconds. The complete engine
  regression passed all 22 cases / 42 mandatory markers in 183.672 seconds.
- The intentional engine assertion, missing map provider and disabled spawn
  protection were rejected by the real runner in 32.313, 27.547 and 28.312 seconds
  respectively. Their precise assertions and successful cleanup were verified.
- The positive and three negative runs used the same ten-binary manifest;
  pinned image/archive identities were unchanged. All owned containers were
  removed, and the positive run had no nonempty AMXX error log.

The first focused attempt exposed an incomplete synthetic joining setup:
`m_bJustConnected` was still set, so GameRules skipped its PlayerSpawn work.
The fixture now completes joining/menu state before bootstrap spawning. The
failure and corrected-run evidence are retained; no product fix was needed.

Documentation/link checks and diff review cover the local changes. The scope
resolver selects full CI rather than a documentation or addon-only fast path.
Full required CI executed the .NET restore/audit, format, build, test, vulnerable
package report, browser and container checks. No C# or .NET behavior changed; the
unchanged full local .NET gate was not repeated and InspectCode was inapplicable.
The workflow retains the new negative run's evidence alongside the existing three
directories. No image pin, production setting or addon version changed here.

This proves bounded interactions between synthetic entities in the real engine.
It does not establish human network input/rendering, encounter frequency,
spawn-camping resistance, player enjoyment, busy-server balance or retention.
The two-human pilot remains separate; successful automation does not close it.

## Hosted Delivery Evidence

[PR #551](https://github.com/tov-vl/gold-src-ops/pull/551) accepted head `7b45d8b6bdff8b6a1968934303bd73723d58c4c3`
as main revision `ef21fce2ed924cce8e58f06688f96cb26309115f` after green required
checks and CLEAN/MERGEABLE. No protection bypass or workflow rerun was used.

| Evidence | PR | Post-merge main |
| --- | --- | --- |
| Full CI | [Passed](https://github.com/tov-vl/gold-src-ops/actions/runs/43830943308) | [Passed](https://github.com/tov-vl/gold-src-ops/actions/runs/43833350726) |
| Positive engine suite | 22 cases / 42 markers, 153.343 s | 22 cases / 42 markers, 154.973 s |
| Three precise negative assertions | Rejected | Rejected |
| Ten compiled payloads and source/policy binding | Verified | Verified |
| Owned-container cleanup | Passed | Passed |

Both compiled and engine-evidence ZIPs were independently downloaded. Payload
hashes, pinned toolchain, exact source identities, all 42 markers, all three
precise negative assertions and cleanup were verified. Each engine result matched
the source-bound compiled manifest and lock from its own run. The successful
runs had no nonempty AMXX error log. Evidence from the disabled protection run
is retained for seven days with the other results; the CI contract now rejects
omission of this directory.

No game-server update, addon publication, application tag, Archive change or
real-client/human gameplay acceptance was performed by this delivery.
