# Uncertain weekly-slot recovery

## Scope and status

Accepted on the target as a separately owner-approved R3 recovery on 2026-11-03.
The previously accepted diagnostic-file recovery remains separate. This work
did not install an addon, change gameplay, create a snapshot or run Steam.

An interrupted weekly attempt can retain a started claim while a later game-host
restart prevents the existing no-capture proof. Current absence cannot establish
what happened during that old attempt. The new terminal state is
`closed-uncertain`, not a successful backup or a claim that capture never ran.

## Contract

- The writer has no transport calls. One reviewed claim, original-result hash,
  last-success hash and current-host proof are checked under the existing lock.
- Preserve exact pending/result/proof bytes and hashes before publishing the
  terminal record. Leave last-success and its capture timestamp unchanged.
- Old-slot replay returns only the validated terminal record. An interrupted
  close keeps the attempt blocked and cannot be retried automatically.
- The monitor independently validates retained evidence and reports code 9.
  Failed service state and copy-age alerts retain their existing meaning.
- Install compatible readers before the writer. After closure, reverting to an
  incompatible reader or restoring a started claim is not an ordinary rollback.
  Retain the new reader and investigate with the original evidence preserved.

The operator command and proof schema are in the
[schedule runbook](game-player-data-schedule.md#закрытие-неопределенного-слота-после-позднего-перезапуска).
No new credential, SSH permission, timer calendar or public metric label is used.

## Evidence and remaining gate

Local Linux fixtures cover stale/future/invalid proof, identity drift, exact
byte retention, unknown side effects, interruption before and after terminal
publication, replay prevention, the next weekly claim and copy-age preservation.
Existing maintenance, schedule and monitor regressions remain applicable.
The local telemetry rehearsal adds code-9 Grafana/OTLP/Prometheus coverage and
alert-rule cases for stale copies and failed services.

Passed locally: 10 uncertain-slot tests, 23 maintenance tests, 21 schedule
tests and 20 monitor tests in an isolated Linux container without network;
24 backup alert-rule scenarios and the complete pinned telemetry/dashboard
rehearsal. The Windows test process used the existing Linux image's Moscow
timezone data through a process-local path. No package or system change was
needed. No C# changed; InspectCode and the complete local .NET gate were not
run. Hosted Quality Gate and all other required product checks passed in
[PR #459](https://github.com/tov-vl/gold-src-ops/pull/459). The accepted merge
tree equals the reviewed source tree. The independent
[main CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41601854312)
is recorded separately from local and target evidence; final delivery closure
requires both it and the documentation PR checks to pass.

Six frozen control-plane files were installed with readers before the writer.
The installed policy had a prior lock-return difference, which was preserved;
that exact target variant also passed all 74 Linux tests. Two advancing ordinary
monitor samples read the unchanged old ledger before closure.

One close completed at 08:19:37 UTC on 2026-11-03. Independent acceptance at
08:22:44 UTC confirmed the retained original bytes and proof, absent pending,
validated old-slot replay prevention and the unchanged last-success record.
The public game state remained current/online, all ten control-plane containers
retained their identities/start times, and game/agent identities, baseline files
and fresh idle agent cycles were preserved. No service reset or restart ran.

The provisioned dashboard exposes the yellow code-9 mapping. The real failed
service still yields code 6, `service_failed=1` and
`PlayerBackupInterventionRequired`. `requires_reconciliation=0` reflects closure
of the old claim only. The retained copy remains stale (`fresh=0`), and
`PlayerBackupCopyUnavailable` remains firing. No alert was reset or suppressed.

One read-only verifier initially assumed root ownership of the existing Grafana
secret. Its observed container UID ownership and read-only mode were pinned
instead; the corrected acceptance passed without permission, credential or
runtime changes. The close command was never repeated.

The next separate gate is a fresh manual player-data copy with an empty-server
maintenance window, off-host Archive/Recover verification and registration.
Closure of an uncertain attempt does not satisfy backup freshness or authorize
a fresh capture. The pending addon installation remains held on that gate.

The owner subsequently continued with that separate step. A fresh manual copy
passed closed capture, one Archive, one Recover and registration on 2026-11-03.
Backup freshness is restored; the historical service failure remains visible.
See the [manual-copy acceptance](player-backup-manual-refresh.md) for its exact
claim boundary and the separate AMXX-engine recovery proof.
