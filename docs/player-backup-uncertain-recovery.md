# Uncertain weekly-slot recovery

## Scope and status

Prepared as a separate R3 recovery on 2026-11-03. Local implementation and
synthetic verification do not authorize a production ledger mutation. The
previously accepted diagnostic-file recovery remains separate. This work does
not install an addon, change gameplay, create a snapshot or run Steam.

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
needed. The full .NET gate, InspectCode and hosted CI were not run; no C# or
application behavior changed, and hosted delivery has not started.

Production installation, one exact old-slot close and independent target
acceptance require a separate owner decision on the frozen recovery plan.
No production backup record has been changed by this preparation. A fresh
manual copy, if selected later, is a separate capture/archive/readback operation.
