# Natural Round Objective Engine Acceptance

This slice exercises natural bomb-map completion under the repository's
unchanged [fast-reentry profile](../ops/gameserver/fast-reentry-loadout-v1.cfg).
Locally accepted on 2026-11-16 against the source following PR #554. Hosted CI
and production acceptance remain separate gates. No profile, shipped addon, producer or event-contract change is included.

## Scope And Stimuli

The previous [multiplayer checks](automated-multiplayer-acceptance.md) use an
infinite round to isolate combat/respawn. The previous [producer checks](round-ended-producer-engine-acceptance.md)
exercise real hooks through an explicit native round-end call. Neither proves
that a bomb or expired round naturally completes with forced respawn enabled.

The new `amxx-round-objectives.sma` fixture runs on `de_dust2` with three fake
clients: one Terrorist and two Counter-Terrorists. A surviving CT keeps the
combat seed from legitimately ending the round by team elimination. The fixture
includes the existing weapon addon, exempting only
these fixture identities from bot exclusion so real combat can seed map stats.
The unchanged producer is loaded as a separate companion plugin and sees all
three clients as bots. The records retain connected-client semantics:
`players=3`, `bots=3`.

Each process executes the exact managed profile. Objective checks start with
`mp_round_infinite=0`, the pinned GameDLL's ordinary default, rather than the
infinite-round setting of the other fixtures. The test checks the managed
one-second forced respawn, unlimited round-respawn window, two-second immunity,
five-second freeze, 35-second C4 and 150-second round duration. It does not
shorten clocks or write winner, round-start, bomb-deadline or defuse-deadline
members.

One ordinary admin restart bootstraps the synthetic teams. The fixture waits
through native `Game_Commencing` until scoring has started, freeze has ended
and no bootstrap restart is pending. Native `rg_check_win_conditions` supplies
the readiness evaluation for synthetic teams. The fixture then enables the
producer, kills one client through native damage and pumps native PreThink for
the fake client's automatic respawn. It does not call manual respawn after this
death. That produces nonzero addon
kill/death statistics to carry across the tested round boundary.

Bomb cases use `rg_plant_bomb` to seed a real native C4 and observe `RG_PlantBomb`.
The defuse case equips a kit and repeatedly invokes the grenade's native
`Ham_Use`; normal grenade Think performs the defuse. The explosion case waits
for the unchanged C4 timer. The timeout case plants nothing and waits for the
full 150-second round timer. Neither the fixture nor its runner calls
`rg_round_end` or `rg_restart_round` to produce the tested outcome.

The exact native paths were checked against the pinned
[ReGameDLL 5.30.0.814 source](https://github.com/rehlds/ReGameDLL_CS/tree/5.30.0.814/regamedll/dlls)
and ReAPI 5.24.0.300 headers. Native planting/use are controlled stimuli, not
acceptance of client aiming, navigation, planting-zone detection or weapon
arming inputs. The on-ground fixture flag permits native defuse use without
simulating client movement.

## Required Outcomes

| Case | Native outcome | Required winning team |
| --- | --- | --- |
| `goldsrcops_bomb_explode` | One planted C4 explodes after its managed timer; `ROUND_TARGET_BOMB` | Terrorists |
| `goldsrcops_bomb_defuse` | One planted C4 is defused, without an explosion; `ROUND_BOMB_DEFUSED` | Counter-Terrorists |
| `goldsrcops_round_timeout` | No planting; full round time expires with `ROUND_TARGET_SAVED` | Counter-Terrorists |

For every case the fixture requires:

- Exactly one observable round-end hook and one team-score increment.
- A natural following restart after the winner delay.
- All clients alive with their own selected primary and pistol restored.
- Fresh spawn immunity/effects after restart and natural expiry during freeze.
- Exact preservation of map kill/death counters across restart, including the
  nonzero combat seed.
- Exactly one complete producer JSON record through the following freeze end;
  no incomplete record remains.

The runtime exports only canonical, non-symlink ready records after checking
original `0600` permissions and a single hard link. Synthetic copies use `0644`
for the unprivileged runner. Per-case size/SHA-256 receipts bind identical bytes.
Independent validation requires one unique canonical UUIDv4 per case, the exact
spool/event shape, `round.ended`, the tested map and the synthetic `3/3` roster.
An extra, missing, changed or reused record rejects acceptance.

## Bounded Runner And Negative Control

Per-case deadlines are explicit: 80 seconds for each bomb case and 200 for the
timeout case. The runner accepts only integer deadlines from 20 through 210
seconds; ordinary cases keep their 45-second process deadline. The existing
600-second engine-container limit, no-network/no-port isolation and resource
limits remain in force. All three new cases must fit inside that shared bound.

`--blocked-objective-check` runs the explosion fixture with objective completion
intentionally disabled by `mp_round_infinite=1` in that disposable process.
The control is enabled only after the normal scored-round bootstrap. It must
observe one actual planting and explosion, zero round completions, then fail
with the precise assertion
`ROUND_OBJECTIVE_ASSERTION_FAILED natural objective completion reached before deadline`.
A setup/compiler failure is insufficient. The integration gate also requires
failed runner status and successful owned-container cleanup. CI retains this
negative evidence for seven days alongside the existing four controls.

The full plan now contains 26 cases / 55 markers and thirteen compiled payloads.
The existing compiled-manifest binding includes the new fixture and unchanged
profile source. All three objective markers are required independently;
missing or duplicated results cannot be replaced by a final success line.

## Acceptance Record

The final frozen source and all thirteen payloads were independently checked
against the compiled manifest. The complete regression passed 26 cases / 55
markers in 469.219 seconds with successful owned-container cleanup and no
nonempty AMXX error log.

| New native case | Scenario duration, including bootstrap and next freeze |
| --- | --- |
| `goldsrcops_bomb_explode` | 60.299 s |
| `goldsrcops_bomb_defuse` | 30.205 s |
| `goldsrcops_round_timeout` | 174.626 s |

The final case itself enforces the full 150-second active round timer. The
reported scenario durations also include preparation, combat/respawn, winner
delay and the following freeze; they are not timer measurements alone.

All five real negative controls were rejected with their exact assertions and
successful cleanup using the same compiled manifest and engine lock. The new
blocked control observed one actual C4 planting/explosion and zero completed
rounds before its expected failure. Seventeen runner regressions passed,
including deadline bounds, missing markers, changed bytes, extra records,
boolean version substitution and reused record identity. The existing two
producer-transfer tests and CI routing/retention contract also passed.

The unchanged agent additionally accepted all ten records from the existing
producer scenario in this same full run and rejected its five malformed
controls, with delivery disabled and temporary queue cleanup. This is separate
from the three new objective records validated above.

No C# was authored; InspectCode is inapplicable. The unchanged full local .NET
gate was not repeated; the existing agent build/importer was exercised directly.
Hosted CI, a deployed server configuration, real-player map flow, fairness and
enjoyment are not proved by this local record. Human gameplay acceptance remains
separate. Archive settings, observation and application runtimes were preserved.
