# GoldSrcOps v2.27 Release Notes

Status as of 2026-09-25: product change merged and the experimental profile
active on the public pilot game host. `v2.27.0` has not been published.

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

The two-second protection and attack-unset behavior are configured and
confirmed by RCON, but the pilot session did not prove their visible or damage
effects. Round endings, spawn fairness, multiplayer balance, and sustained
play were not tested. The mode remains experimental and the exact classic
profile remains available for guarded restoration. No release candidate,
stable tag, or published-image acceptance is claimed yet.

See [release readiness](v2.27-readiness.md) for the remaining publication
gates and [product design](v2.27-fast-reentry.md) for the bounded behavior.
