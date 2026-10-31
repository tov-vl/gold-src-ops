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
also passed all six verification jobs at the accepted revision. Source delivery
is complete; no runtime rollout or game-client pass is claimed.
No C# source is changed by this slice; a new InspectCode run is not applicable.

## Pending live gate

Steam remains on hold because the account is in use elsewhere. No CS request,
SSH session, target update, game fixture, lab Docker lifecycle or access-rule
change was performed while implementing this controller. Ordinary local .NET
tests retain their existing disposable database-container boundary. All six
fresh autonomous panel acceptances remain pending. When the owner confirms the
account is free, prepare a fresh campaign, run one case, review its five frames
and independent cleanup,
then proceed through the other five cases. The earlier cancelled request is
retained and is not a retry input for the new campaign.
