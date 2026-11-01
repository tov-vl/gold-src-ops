# Client campaign controller readiness

## Scope

Repository-owned PowerShell 7 orchestration for the six RU/EN MOTD
cases. Local preparation, Steam hold, fixed one-attempt dispatch, same-ID
collection/reconciliation, explicit cancellation, bounded fixture cleanup,
evidence integrity and separate visual review are included. The Windows runner,
trust engine, permissions, addon 0.38.0, production and cloud resources are
unchanged. No target installation or runtime rollout is part of this slice.

Live use is state-changing (R2). Offline checks establish controller
behavior only; they do not substitute for that target gate. Rollback for this
source-only delivery is retaining the existing individual entry points and
private receipts. Never erase claims or repeat an uncertain runtime attempt.

## Verification

Focused controller tests exercise the fixed matrix and default hold, inert
planning/status, interrupted fixture start, uncertain dispatch, read loss,
collection deadline, stop/absence failures, stale heartbeat, strict cleanup
booleans, duplicate images, wrong baseline, missing actions/markers, concurrent
locks, cancellation, same-ID resume, evidence tampering and receipt recovery.
Adapter tests replace process and OS ports, checking command arguments, local
Docker selection, occupied resources, queue readiness, heartbeat freshness and
installed code pins. All are offline and send no native input.

Local verification passed on 2026-10-31:

- 86 controller assertions and 10 adapter assertions, with all external effect
  ports replaced; no target traffic or native input.
- The complete existing client policy suite under PowerShell 7 and Windows
  PowerShell 5.1, native compilation in both runtimes, 442 synthetic native
  checks and seven Python fixture tests.
- The local .NET Quality Gate: restore, formatting, build with zero warnings and
  errors, 1,294 passing tests, 62 opt-in browser skips and no vulnerable packages.
- One local-only preparation using the retained accepted bundle and pinned
  inputs: six new IDs remained `prepared`, all unaccepted, with Steam held.

The [product PR #426](https://github.com/tov-vl/gold-src-ops/pull/426) was accepted
as `0456c8856db2fdcd0358dd77c3aa0ddcd684e714` after
[full PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41234868537)
passed all six verification jobs. Publication jobs were correctly skipped for
this source-only PR. The
[post-merge run](https://github.com/tov-vl/gold-src-ops/actions/runs/41236111915)
also passed all six verification jobs at the accepted revision. That source-only
delivery did not include a runtime rollout or a game-client pass.
No C# source is changed by this slice; a new InspectCode run is not applicable.

## First live trial and verifier correction

After the owner confirmed Steam availability, one EN `ranks` request completed
on 2026-10-31 UTC. The real controller performed readiness checks, one dispatch,
five-frame collection and fixture cleanup. The runner reported completed native
interaction, restored configuration and game cleanup. A fresh post-completion
heartbeat showed no game process. Independent fixture/network/relay absence,
both alias/cooldown and movement epochs, and zero AMXX errors were confirmed.

Final local verification failed closed: it incorrectly expected generic
`panel-first`/`panel-alias` labels, while the existing runner correctly emitted
`ranks-first`/`ranks-alias`. Synthetic controller results had repeated this
incorrect assumption. The correction requires the selected panel's labels;
tests now use the real runner label functions for all six panel/language pairs
and reject generic, foreign-panel and reordered labels.

The corrected read-only verifier validated the retained package. All five real
frames were separately reviewed, confirming readability, both OK closes,
alternate-alias reopening and returned movement. This independently accepts
the EN native client scenario. The original campaign remains
`needs_reconciliation`: its source pins, claims and raw results were preserved,
and no game request was repeated. Read-only inspection reports source drift and
cannot mark the campaign accepted or enable runtime operations with new source.

The correction passed 112 controller and 10 adapter assertions locally, the
complete client policy/native/Python suite and the .NET Quality Gate (1,294
passed, 62 opt-in browser skips, no build warnings/errors or vulnerable
packages). The correction was accepted in
[PR #428](https://github.com/tov-vl/gold-src-ops/pull/428) as
`486369c3a4ce4cdc88a32019a3d557f46d148170`, after
[full PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41242227504)
passed. Its [post-merge CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41243413600)
also passed all six verification jobs at the accepted revision. C# and the
target's nine installed runner files are unchanged; no new InspectCode run or
target update is applicable. No production, addon, cloud or access change was
made. This closes delivery of the verifier correction, not its separate live
controller gate.

## Successful live controller gate - 2026-11-01 (MSK)

A fresh campaign using the accepted correction completed one RU `ranks`
request through the public CLI. Its source and prepared input pins matched.
The controller reached `captured_unreviewed` after readiness checks, one
dispatch, collection and cleanup. Five real frames were independently reviewed:
the RU title and body were readable, both OK closes and alternate-alias reopening
were observed, and movement returned after both closes. The matching baseline,
ordered interaction, both alias/cooldown and movement epochs, zero AMXX errors,
restored configuration and fixture/network/relay absence were verified. A fresh
post-completion worker heartbeat confirmed no game process.

The normal `review` operation rehashed the retained evidence and created its
immutable five-frame review receipt. The campaign then reported `accepted` with
`acceptancePassed=true`; raw worker acceptance/renderer flags remained false.
No direct state edit, source-pin migration, repeated dispatch or manual window
selection was requested. This closes the corrected controller's first live
end-to-end gate as well as RU `ranks` native acceptance. Both ranks languages are
accepted; EN retains its separate earlier independent review.

No code, installed runner, trust engine, access rule, addon, production service
or cloud resource changed. This follow-up is evidence only. The already accepted
source's build/unit/native/InspectCode checks were not repeated; the bounded
live trial, five-frame review and matching cleanup evidence are the new checks.

## Remaining-case acceptance - 2026-11-01 (MSK)

The four remaining cases, RU/EN `profile` and RU/EN `topall`, each completed
with one dispatch through the corrected controller. All 20 new native frames
were independently reviewed. Every case passed readable localized content,
both OK closes, alternate-alias reopening, cooldown and returned movement.
Matching source/input pins, baseline identities, ordered actions, both server
movement epochs, zero AMXX errors, restored configuration, fixture/network/relay
absence and a fresh zero-game heartbeat were verified before hash-bound review.
All four normal reviews reached `accepted`. No manual window selection,
source-pin migration, direct state edit or repeated dispatch was used.

After the two profile cases, the active queue held eight terminal requests.
One predecessor-bound signed maintenance package archived exactly two owned,
already accepted requests. No pending work or other request was moved. Readback
confirmed identical result bytes and accessible matching images, six unchanged
other requests, all nine unchanged runner hashes and an enabled, unblocked
revision-16 worker. Queue occupancy fell from eight to six before the two
topall cases. This was queue maintenance, not a runner-code update.

Together with the earlier independent EN ranks review and RU ranks controller
pass, all six native scenarios and 30 interaction frames are accepted. The old
EN campaign remains `needs_reconciliation` with its original source pins,
claims and raw results; its evidence was not imported into a different request.
The unused fresh-campaign EN ranks entry remains prepared and was not replayed.
Raw worker acceptance/renderer flags remain false; review receipts are separate.
The campaign is held after completion and no game process remains.

This evidence-only follow-up changes no source, installed runner code, trust
engine, permission, addon, production service or cloud resource. Existing
build/unit/native suites were not repeated on unchanged code; no new C# work
requires InspectCode. Renderer identity, production saved personal statistics,
PvP and activity aggregation remain outside this bounded fixture acceptance.
