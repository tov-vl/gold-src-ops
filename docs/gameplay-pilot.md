# Two-Player Gameplay Pilot

Preparation baseline: `2b33081022ad54061fa8c8e1f4efef4a3f3ad1e0`, 2026-11-17.
This is a ready-to-use protocol, not a completed player acceptance record.
Use the [result template](templates/gameplay-pilot-result.md) for one session.
Share the [Russian participant brief](gameplay-pilot-participants-ru.md) with
actors A/B when arranging the session. It adds no automation or completed cases.

## Outcome And Boundary

Run one controlled 30-45 minute session with two consenting human participants
and independent Steam accounts. Confirm that the existing fast-reentry mode is
understandable and usable, record failures and select at most one follow-up.
Two people cannot establish busy-server balance, demand, retention or an SLO.

Use the existing game server, accepted addons and managed profile. No addon
release, bot, account sharing, Steam offline workaround, server restart,
administrative map change, CVAR change, vault edit, backup, queue mutation or
telemetry replay is part of this pilot. The accepted client lab remains a
visual regression tool; two clients on the same Steam account are not the two
participants. Preparation does not launch either client or book a session.

The [roadmap](roadmap.md#gameplay-pilot-and-follow-up),
[rules contract](v2.93-gameplay-rules.md),
[respawn guide](v2.99-respawn-rules.md),
[profile contract](v2.100-profile-loadout.md) and
[map menu](../samples/amxmodx-map-menu/README.md) define the baseline.
Automated [multiplayer](automated-multiplayer-acceptance.md),
[objectives](round-objectives-engine-acceptance.md) and
[telemetry isolation](telemetry-server-isolation-acceptance.md) remain technical
evidence, separate from the observations recorded here.

## Freeze Immediately Before Playing

Run the [server preflight](../tools/release/check-gameplay-pilot.py) immediately
before connecting the two clients. Add its `pilotHeader` to the owner-controlled
session record, then fill in the participant and client fields. Keep exact host
endpoints, Steam IDs, installation paths and unsanitized screenshots in that
private record. Public results use actors A/B and sanitized references.

```powershell
python -B ./tools/release/check-gameplay-pilot.py --profile $Profile --profile-sha256 $ProfileSha --execute --output $Report
```

Use a reviewed private JSON profile and its independently retained SHA-256.
The profile has `schema` equal to `1`, the existing [game acceptance profile](release-acceptance.md#game-and-control-plane-boundary)
in `acceptance`, the pinned `sshConfigSha256`, `mapAddon` with `version`,
`manifestSha256` and the four `payload` hashes (`plugin`, `config`, `loader`,
`dictionary`), and `engine` with `protocol`, `rehlds` and `build`. Freeze
those values from accepted install evidence. Omitting `--execute` validates
the two local files. `--output` creates a fresh JSON file, with Markdown in
`pilotHeader` for copying into the result template.

The command checks protected receipts and installed hashes, then uses the
existing control-plane RCON path with fixed read-only status, version and CVAR
queries. A2S_INFO supplies the map and player/bot counts before and after those
queries. Finish this check before the players join. The profile, service
invocation and map must agree throughout the sample. The returned `ready`,
`drift` or `unavailable` result identifies the observed differences and missing
fields. Drift or unavailable evidence returns a nonzero exit status. The
engine's [`mp_timeleft`](https://github.com/rehlds/ReGameDLL_CS/blob/master/README.md)
is recorded at the time of the server sample. An unavailable collection also
returns a bounded reason for the failed stage, such as an SSH timeout or an
invalid response.

Record UTC start/end, server/profile identifier, current map and `/timeleft`,
installed weapon/map-addon versions and receipt/payload hashes, engine version,
effective gameplay values and both clients' renderer/language. Confirm that
these match the accepted target. A mismatch is a preflight blocker to inspect,
not permission to install or adjust anything during the pilot.

Expected reference values come from the accepted
[managed loadout profile](../ops/gameserver/fast-reentry-loadout-v1.cfg):

| Setting | Reference | Interpretation |
| --- | --- | --- |
| `mp_forcerespawn` | `1` | Configured automatic-respawn delay; observe death animation and engine scheduling separately |
| `mp_respawn_immunitytime` | `2` | Configured spawn protection duration |
| `mp_respawn_immunity_force_unset` | `2` | Primary/secondary attack removes protection; movement alone does not |
| `mp_freezetime` | `5` | Round freeze time, not the respawn delay |
| `mp_friendlyfire` | `0` | Teammate shots do not cause ordinary friendly-fire damage |
| `mp_roundtime` / `mp_c4timer` | `2.5` minutes / `35` seconds | Normal engine-owned round and bomb timers |
| `mp_timelimit` | `25` minutes | A natural map change may occur during a longer pilot |
| `mp_autoteambalance` / `mp_limitteams` | `1` / `2` | Engine owns team admission and balancing |

Also capture actual addon enablement, healing amount, ammo-refill state and
read/write availability for saved preferences/stats. The source defaults
healing to 15 HP, caps health at 100 and refills owned loadout reserves; live
status must confirm the effective values. `/rules` must agree with those
values. Never overwrite a live setting to fit this table.

The exact map set remains `de_dust2`, `de_inferno`, `de_nuke`, `de_train`,
`cs_office`. Use an available `de_*` map for bomb cases. If a suitable map is
unavailable through the existing cycle/native vote, record those cases as
`Not tested`. Do not change mapcycle or introduce a test map. Check remaining
map time, do objective cases early and record any natural map transition.

### Server Preflight Evidence On 2026-11-22

The read-only sample completed at 16:48 UTC with `ready` and 77 matched
checks. It verified the accepted weapon addon 0.40.0, map addon 0.2.0,
their installed hashes, the nVault storage contract and the live gameplay
settings. The game service invocation and map stayed consistent across the
sample. Both A2S snapshots showed `de_inferno`, zero players and zero bots.
The observed map time remaining was `07:55`.

An initial attempt ended at the first SSH collection before A2S evidence or
any RCON request. The completed sample followed a transport check. The
fixture suite passed 28 acceptance tests and eight transport tests, including
drift, incomplete replies and a map change between snapshots. Player
scenarios remain pending until the two participants are available.

## Session Order

| Elapsed time | Work |
| --- | --- |
| 0-5 minutes | Freeze baseline, connect A/B, choose opposite teams, capture initial saved/map stats and loadout |
| 5-11 minutes | Menus, next-spawn loadout, respawn and protection; swap attacker/victim |
| 11-18 minutes | Eligible rewards, exclusions and team behavior |
| 18-28 minutes | Natural bomb explosion, defuse and round-time expiry on a suitable map |
| 28-35 minutes | Reconnect, saved state and RU/EN guidance |
| 35-40 minutes | Native map menu/vote, time-left and any actual transition |
| 40-45 minutes | Stop testing, compare observations, prioritize one follow-up |

This is a time budget, not permission to shorten timers. At minute 45 stop and
mark unfinished cases `Not tested`. If a round/map transition interrupts a
case, record its boundary and restart only that player scenario if time remains;
do not use an administrative restart or erase prior results.

## Player Checks

For each row record actual actions, observed result, actors, map/round and a
sanitized evidence reference. A result based only on an earlier automated test
stays `Not tested` in this pilot. Repeat P03-P06 with actors exchanged when
possible and retain both observations.

| ID | Actions and preconditions | Expected result / observation |
| --- | --- | --- |
| P01 | A/B join normally with separate accounts; capture initial `/profile`, `/ranks` and `/rules` | Both can play; views are private/readable and initial values are recorded |
| P02 | While alive, choose a different primary/pistol in `/guns`; inspect `/profile`, then die and respawn normally | Selection describes the next spawn, does not immediately replace the alive inventory and is applied on the next spawn; other actor is unchanged |
| P03 | A kills enemy B; observe death, new spawn and first controllable frame; repeat with roles exchanged | Automatic return works; record at least three timings if time permits, using the same measurement boundaries; do not equate animation time with the configured one second |
| P04 | Immediately after a spawn, the protected actor stands/moves without attacking while the enemy attempts controlled damage; repeat after protection expires | Damage is blocked during protection and works afterward; movement alone does not cancel mode-2 protection; visuals alone do not prove immunity |
| P05 | On separate fresh spawns, use primary and secondary attack while protection is active; enemy then attempts damage | Each supported attack removes protection early; record weapon/action and observed damage; inability to arrange the timing is `Not tested` |
| P06 | An alive human attacker below 100 HP, with a depleted owned reserve, kills the enemy after protection expires; record HP, active weapon, clip and reserve | Enabled healing adds the effective eligible amount up to 100; enabled reserves top up to their loadout limits. A disabled reward leaves its value unchanged. No automatic clip reload or active-weapon replacement; account for the killing shot's normal clip consumption |
| P07 | Capture actor A's counters/HP/reserves; A uses ordinary client suicide once and respawns | No enemy-kill reward, kill increment or streak promotion for suicide; the victim death/life reset is separate from new-spawn equipment |
| P08 | If normal team admission permits, join the same team and attempt controlled teammate damage; then return to opposing teams | With friendly fire disabled, teammate shots cause no ordinary damage/reward. If admission/balancing prevents the setup, record `Not tested`; do not change team CVARs |
| P09 | If naturally reachable, one actor takes a world/environment death while the other observes | No enemy reward for the other actor; life/streak reset is observed. Do not enable cheats or add hazards to manufacture it |
| P10 | T plants C4 through normal player input; CT deliberately lets it explode | Native explosion ends the round; record visible outcome, next-round transition and usable loadout/protection afterward |
| P11 | On another ordinary round, T plants and CT defuses through normal input | Native defuse ends the round; record outcome and next-round continuity |
| P12 | On a bomb map, keep both teams alive without planting until normal round time expires | Engine produces its natural timeout verdict and next round; record actual result, not a fixture command or a forced end |
| P13 | Inspect map/session and saved counters after eligible enemy kills/deaths; open `/ranks` | Each panel matches its stated scope; saved progression and rank are consistent with actual eligible events. Rank promotion is only tested if a threshold is naturally crossed |
| P14 | Record saved state and selected loadout/language, disconnect one actor normally, reconnect the same account and inspect again | Previously confirmed saved fields are restored; preserve the distinction from connection/map-local counters. If storage says session-only/unavailable, do not claim persistence passed |
| P15 | A uses RU, B uses EN; read `/rules` and `/profile`, close normally and regain movement/input; try an alternate alias after the shared cooldown | Guidance is understandable, next-spawn/saved wording is correct and opening/closing one actor's panel does not alter the other's language, inventory or control |
| P16 | Open `/maps`, inspect the exact five entries/current-map exclusion, request another allowed map and query `/timeleft` | Menu delegates to native voting and time-left. Record the engine's admission/quorum/cooldown response; a submitted request is not proof of an accepted vote |
| P17 | If the native vote is admitted and reaches quorum, observe the actual map change and both clients afterward | Correct map loads, clients can rejoin/play and confirmed saved state remains consistent. Record the exact transition; valid engine refusal leaves this case `Not tested` |

Read-only operator telemetry is optional supporting evidence for P10-P12.
Record its event window and coverage separately; do not infer exact player
behavior, complete source data or successful ingestion just from a panel.
Do not replay events or edit counters to align the evidence.

## Stop, Classify And Select One Follow-up

Stop controlled scenarios if an unrelated player joins, either participant
withdraws, the account becomes busy elsewhere, a client/server crashes, state
is lost, or an unexpected authorization/configuration issue appears. Preserve
observations and let ordinary server operation continue. Kicking others,
restarting services and repairing durable state need their own reviewed action.

Use `Passed`, `Failed` or `Not tested` per row. A failed expected behavior is
not reclassified as untested. A blocked setup, insufficient time or unavailable
precondition is untested with its reason. Do not replace a failed attempt with
a later successful repeat; record both and explain the discrepancy.

The core pilot requires P01-P06, P10-P16. P07-P09 and P17 are explicit
conditional cases; their missing evidence limits exclusions/map-transition
claims even if the core passes. Report `Core passed with limits`, `Partial`
or `Failed`, and list every failed/untested case. No session is already passed
by this preparation, and conditional cases do not silently become accepted.

Choose one follow-up using: correctness/state-loss or exploit impact first,
then repeated inability to resume combat or understand a rule, then observed
map/spawn friction. Record frequency, affected actors, evidence confidence,
player impact, smallest change, its acceptance criterion and rollback.
Measure time to the next encounter and repeated spawn kills as observations,
not universal numeric targets. Two people do not establish balance.

Prefer one reproducible issue supported by both actors or repeated evidence.
If no concrete issue is found, record `No change selected`; do not manufacture
a plugin or map change. A proposed map adjustment must preserve the five-map
contract. An external plugin requires its own source/license, compatibility,
hook/assets and rollback review. Publication of results uses a sanitized summary;
private identity mappings and raw evidence stay outside Git.
