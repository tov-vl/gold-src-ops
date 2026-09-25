# GoldSrcOps v2.27 Release Notes

Status as of 2026-09-25: stable
[`v2.27.0`](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.27.0)
published. The experimental profile is active on the public pilot game host.

## What Changed

- Added the fixed `fast-reentry-v1` team-based profile using native ReGameDLL
  respawn settings and a map-change reapplication hook.
- Added a plan-first, guarded transition between the known-good
  `public-classic-v1` profile and the experimental fast profile.
- Bound the active game configuration, profile, mapcycle, and policy marker to
  reviewed hashes while preserving the game-event agent, its identity, and
  unsettled queue/spool evidence.

No API, Web, AlertReceiver, database, authentication, firewall, or
control-plane worker change is included. This is not a Deathmatch plugin,
rank system, custom-spawn system, or player-identity feature.

## Current Evidence And Limits

[PR #226](https://github.com/tov-vl/gold-src-ops/pull/226) and its
[post-merge CI](https://github.com/tov-vl/gold-src-ops/actions/runs/36065360554)
passed. The reviewed game-host transition passed external A2S, read-only
RCON, guard, and settled queue/spool checks. One player joined, used `kill`
several times, observed immediate respawn, and exited; post-session checks
returned to zero players and bots with the profile and guard still valid.

Signed `v2.27.0-rc.1` and `v2.27.0` target the same reviewed revision
`08a4119f5ba9b07cd993bd31053732470c8e2878`. The
[candidate CI](https://github.com/tov-vl/gold-src-ops/actions/runs/36146067382)
and [stable CI](https://github.com/tov-vl/gold-src-ops/actions/runs/36150171803)
passed. Stable API, Web, and AlertReceiver references reuse the accepted
candidate digests without rebuilding. Those control-plane runtimes were not
redeployed, and the game-host transition and player session were not repeated
for release publication.

The two-second protection and attack-unset behavior are configured and
confirmed by RCON, but the pilot session did not prove their visible or damage
effects. Round endings, spawn fairness, multiplayer balance, and sustained
play were not tested. The mode remains experimental and the exact classic
profile remains available for guarded restoration.

See [release readiness](v2.27-readiness.md) for immutable digests and bounded
evidence, and [product design](v2.27-fast-reentry.md) for the behavior.
