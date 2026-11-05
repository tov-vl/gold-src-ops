# Weekly backup service-state reconciliation

## Outcome

Accepted on 2026-11-05 after a separate owner decision: one fixed
`reset-failed` operation cleared only the retained systemd failure of
`goldsrcops-player-data-backup.service`. It did not start the service or change
the outcome of the uncertain November 1 backup attempt.

The ordinary monitor subsequently published a fresh valid observation with
`service_failed=0`, `requires_reconciliation=0`, `last_attempt_status=9`
(`closed-uncertain`), `copy_source=2` (manual) and `fresh=1`. The latest capture
timestamp was unchanged. `PlayerBackupInterventionRequired` disappeared from
Prometheus, and the existing IRM group automatically resolved at 14:47:23 MSK.
Its latest alert explicitly reports resolved on the source. No manual alert
resolution or new notification test was performed; retained history remains
available. A separate recovery email is not claimed.

## Reviewed boundary

The [uncertain-slot recovery](player-backup-uncertain-recovery.md),
[verified manual copy](player-backup-manual-refresh.md) and its
[isolated engine recovery](v2.101-player-data-engine-recovery.md) had already
passed their separate acceptance gates. The remaining warning came from the
service's retained `exit-code` result and failed state, with the original
execution timestamps from November 1. It was not evidence of a new attempt.

Fresh read-only preflight required:

- valid `closed-uncertain` evidence for the old slot and no pending claim;
- no old-slot capture or pending marker on the game host;
- a fresh, validated manual Archive/Recover registration;
- an enabled, waiting weekly timer with its original calendar;
- no executing backup service and no current reconciliation requirement;
- active game/agent processes and unchanged accepted monitoring runtime.

Before the reset, the operator retained original unit properties, bounded
service journal output and exact bytes/hashes of all 21 JSON ledger/evidence
records in the private operator record. The operation took the existing
schedule lock, rechecked the reviewed identities and made one durable claim
before issuing the single fixed command. An uncertain outcome would have
blocked automatic repetition. Resetting systemd's retained failure cannot
reconstruct that original manager state; the saved audit and unchanged ledger
are the preservation boundary, not a replacement artificial service failure.

## Independent acceptance

The service became inactive with a successful manager result and no new
execution timestamps or process. Independent postflight confirmed unchanged:

- all 21 JSON records, ledger file metadata and old weekly last-success;
- the manual copy's exact capture timestamp and accepted registration;
- timer invocation, configuration and next scheduled activation;
- game/agent process and invocation identities;
- all ten control-plane container identities, seven dashboards, rule
  definitions and worker flags.

The timer still points to 2026-11-08 at 04:00 MSK and retains the empty-server
condition. No service start/stop/restart, capture, Archive, Recover, old-slot
replay, journal rewrite, timer change, new permission or provider setting was
performed. The new overview now shows no backup warnings while retaining the
old uncertain attempt and the distinct manual-copy source.

## Verification limits

This is a bounded R3 operational recovery followed by documentation-only
publication. Runtime acceptance came from guarded preflight, one confirmed
command, independent host/control-plane reads, the ordinary monitor cycle,
Prometheus and read-only IRM inspection. No product source, package, addon or
container image changed. The full local .NET suite, InspectCode, engine and
client-lab checks were not rerun for this operation. Documentation links and
required documentation PR/main CI are separate publication gates.

The old slot remains `closed-uncertain`, not a verified backup or proof that
no capture happened. A successful future weekly capture and its own acceptance
remain separate from this service-state cleanup. No production restore,
original crash reconstruction, RPO/RTO or new email-delivery claim is made.
