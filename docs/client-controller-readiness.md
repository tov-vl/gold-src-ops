# Client campaign controller readiness

## Scope

Repository-owned PowerShell 7 orchestration for the six deferred RU/EN MOTD
cases. Local preparation, Steam hold, fixed one-attempt dispatch, same-ID
collection/reconciliation, explicit cancellation, bounded fixture cleanup,
evidence integrity and separate visual review are included. The Windows runner,
trust engine, permissions, addon 0.38.0, production and cloud resources are
unchanged. No target installation or runtime rollout is part of this slice.

The future live use is state-changing (R2). Offline checks establish controller
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

## Pending live gate

Five native client scenarios remain: RU `ranks`, RU/EN `profile` and RU/EN
`topall`. A fresh campaign is required for future runtime work after the source
correction. End-to-end acceptance of the corrected controller remains separate
from the successful read-only verification of this retained EN result. The
earlier Steam-conflict cancellation and both attempts' permanent claims remain
preserved. The operator hold is restored while resolving the controller issue;
it is not a claim that Steam is busy again.

A new private campaign is prepared against the accepted correction, with six
unclaimed IDs and an active operator hold. Its continuation plan lists only the
five pending native scenarios and links the independent EN review separately;
the EN result was not imported into a different request or dispatched again.
