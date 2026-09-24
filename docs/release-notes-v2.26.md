# GoldSrcOps v2.26.0 Release Notes

Status as of 2026-09-24: [`v2.26.0`](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.26.0)
published from accepted `v2.26.0-rc.6` after controlled reboot and one
real-round check. Stable publication promoted all three candidate image
digests without rebuilding and passed independent published-image smoke.

## Overview

GoldSrcOps v2.26 makes the existing anonymous gameplay-event path persistent
on the accepted single game host. The producer refuses new intake when its
reviewed incoming spool is full, suppresses rounds with no human players, and
the companion agent exposes a versioned, aggregate-only JSON status snapshot.
The R3 host workflow preserves the public game profile and guarded autostart,
keeps the game independent of agent availability, and retains durable evidence
through rollback.

## Included In v2.26

- `goldsrcops_spool_max_pending`, defaulting to 1,000 with a fixed valid range
  of 1 through 10,000.
- Fail-closed incoming-directory inspection before event identity allocation or
  file creation.
- Suppression without deletion, overwrite, rename, or quarantine when intake
  is full or cannot be inspected safely.
- Suppression of empty-human rounds before event identity allocation.
- `GoldSrcOps.GameEventAgent status --json` schema version 1.
- Aggregate runtime gates, SQLite capacity and queue counts, sequence state,
  spool receipt counts, and spool-directory counts.
- Explicit omission of event payloads, identities, paths, endpoints, OAuth
  settings, credential references, maps, and player or bot counts from status.
- A frozen R3 policy for persistent overlay, identity, service independence,
  reboot recovery, evidence-preserving rollback, and one-round acceptance.
- A plan-first persistent host workflow with exact hash guards, independent
  game and agent boot enablement, stdin-only secret forwarding, aggregate
  status verification, and active evidence-preserving rollback.

## Compatibility And Rollback

The product change has no database migration and changes no API, Web,
AlertReceiver, Caddy, control-plane worker, public route, or authorization
role. The new producer setting defaults to a bounded value, while producer and
delivery activation remain off in every bundled default. Game-host state
changes only through the separately reviewed persistent activation.

The existing v2.11 pilot workflow remains intentionally temporary and
boot-disabled. It is not silently promoted into a permanent workflow. The
dedicated persistent workflow retains `public-classic-v1`, rebinds
`guarded-autostart-v1` to the exact overlay, keeps game availability independent
from agent availability, and preserves unresolved queue or spool evidence during
rollback.

## Current Evidence

Product PR [#216](https://github.com/tov-vl/gold-src-ops/pull/216) merged as
`c7003b4dbdf76f1b5626d8a9c813aa8535b9af11`. Its focused producer and agent
tests, pinned producer compilation, complete Quality Gate, package audit,
container smoke, and browser smoke passed. Post-merge workflow
[#35846327867](https://github.com/tov-vl/gold-src-ops/actions/runs/35846327867)
also passed all four required jobs.

The persistent host workflow and deterministic smoke were reviewed and merged.
Earlier candidates remain immutable rejection evidence: `rc.1` lacked the
external pre-delivery gate; `rc.2` exposed incomplete guard-backup rollback;
`rc.3` failed at the agent guard; and `rc.4` failed at delivery-enabled status
capture. Failed applies restored the accepted public game. `rc.5` fixed the
status credential context and activated, but real play revealed an unwanted
empty-round event. [PR #224](https://github.com/tov-vl/gold-src-ops/pull/224)
fixed that producer behavior before `rc.6` was accepted.

Signed `v2.26.0-rc.6` points at revision
`3eacb59b1863fb13496399b8404f388c27e76546`. Its
[candidate workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/36020976332)
passed the complete Quality Gate, container and browser smoke, publication, and
independent verification of the API, Web, and AlertReceiver images. None of
those control-plane images was redeployed. The exact game-host bundle passed
manifest verification, rollback rehearsal, external A2S and authenticated
RCON gates, persistent activation, and a controlled reboot. The guard and both
enabled services remained healthy with zero unexpected restarts. One real
round produced exactly one new anonymous `RoundEnded` record and accepted
delivery, visible in Reader; an empty interval afterward produced no further
event. Agent queue and spool aggregates settled at zero.

Signed stable `v2.26.0` targets the same revision as `rc.6`. Its
[stable workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/36032772302)
used the stable-promotion fast path, reused all three immutable candidate
digests, and passed their independent smoke jobs. The
[GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.26.0)
was published separately after those checks.
Stable promotion changed no running game or control-plane component.

## Known Limits

- Only anonymous `round.ended` is in scope.
- The pending-spool limit does not cap SQLite dead letters or rejected-file
  evidence; those remain explicit operator states.
- Aggregate status is evidence, not a health Boolean, and must not prevent
  startup reconciliation.
- A single real-round and reboot exercise cannot prove sustained throughput,
  abrupt power-loss durability, high availability, long-term reliability, or
  an achieved SLO.
- The reference deployment remains one game host and one control plane.

## References

- [v2.26 release readiness](v2.26-readiness.md)
- [Persistent Gameplay Telemetry product boundary](v2.26-persistent-gameplay-telemetry.md)
- [Game-event contract and agent](game-events.md)
- [v2.11 game-event pilot](v2.11-game-event-pilot.md)
- [v2.24 guarded game-server autostart](v2.24-guarded-gameserver-autostart.md)
- [Product pull request](https://github.com/tov-vl/gold-src-ops/pull/216)
- [Product post-merge CI](https://github.com/tov-vl/gold-src-ops/actions/runs/35846327867)
- [Accepted candidate CI](https://github.com/tov-vl/gold-src-ops/actions/runs/36020976332)
- [Stable promotion CI](https://github.com/tov-vl/gold-src-ops/actions/runs/36032772302)
- [GitHub Release v2.26.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.26.0)
- [Project backlog](backlog.md)
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [Security](security.md)
