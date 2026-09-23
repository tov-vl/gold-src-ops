# GoldSrcOps v2.26.0 Release Notes

Status as of 2026-09-23: product implementation and persistent host workflow
merged. `v2.26.0-rc.1` was published but not activated because its host script
did not enforce the required external game check before enabling producer and
delivery. Published `rc.2` reached spool-only in a rollback rehearsal but was
not accepted after an incomplete guard-backup restoration was found. A proposed
`rc.3` fix, target acceptance, and stable promotion remain pending.

## Overview

GoldSrcOps v2.26 prepares the existing anonymous gameplay-event path for
bounded long-lived operation. The producer now refuses new intake when its
reviewed incoming spool is full, and the companion agent exposes one
versioned, aggregate-only JSON status snapshot for host policy and operator
evidence.

The merged product increment does not activate telemetry. A separate R3 host
workflow must install a persistent overlay, preserve the accepted public game
profile and guarded autostart, enable the agent across boot without coupling it
to game availability, rehearse rollback, survive one controlled reboot, and
accept one fresh real round.

## Included In v2.26

- `goldsrcops_spool_max_pending`, defaulting to 1,000 with a fixed valid range
  of 1 through 10,000.
- Fail-closed incoming-directory inspection before event identity allocation or
  file creation.
- Suppression without deletion, overwrite, rename, or quarantine when intake
  is full or cannot be inspected safely.
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
AlertReceiver, Caddy, control-plane worker, public route, authorization role,
or existing game-host state. The new producer setting defaults to a bounded
value, while producer and delivery activation remain off in every bundled
default.

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
Stable `v2.25.0` and immutable `v2.26.0-rc.1` and `rc.2` tags exist. The `rc.2`
bundle was verified and installed dormant. A spool-only rehearsal reached the
external gate, and its timeout rolled back before producer or delivery was
enabled. The public game remained reachable. Independent inspection found that
the rollback restored the guard marker but omitted its referenced backup. The
exact-hash backup was recovered owner-only without another game restart, and
the accepted guarded baseline was verified again. The local `rc.3` fix now
preserves that backup across activation and rollback; review, required CI, a
new exact bundle, repeat rehearsal, controlled reboot, one-round delivery,
stable promotion, and release evidence remain pending.

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
- [Project backlog](backlog.md)
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [Security](security.md)
