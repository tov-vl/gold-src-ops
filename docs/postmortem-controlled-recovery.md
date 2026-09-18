# Controlled Failure And Recovery Postmortem

Exercise date: 2026-09-02. Status: completed. Classification: planned production
recovery exercise, not an unplanned customer incident.

## Summary

GoldSrcOps deliberately stopped its controlled game service behind a bounded
automatic-recovery watchdog. Three consecutive failed A2S polls opened one
availability incident and transactionally created one unavailable alert event.
The pending event survived an API restart while delivery was disabled. Service
recovery closed the same incident, and a temporary restricted HTTPS receiver
accepted the unavailable and recovered events once each with distinct
idempotency keys.

The exercise also rolled the control plane back from the current candidate to
the previous verified image and forward again. Readiness and migrations passed,
and durable counts were unchanged. The temporary receiver was removed and
alert delivery returned to its disabled baseline.

## Impact

- The controlled endpoint was intentionally unavailable for the exercise.
- The incident remained open for 195 seconds.
- Exactly one incident opened and the same incident recovered.
- No incomplete command or dead letter was created.
- The exercise did not represent a customer outage or real-player workload.

## Timeline

1. The game service was stopped behind a 12-minute automatic recovery watchdog.
2. Polling continued at the configured 60-second interval.
3. The third consecutive failure opened one incident and committed one pending
   unavailable event.
4. The API restarted with alert delivery disabled. Readiness recovered, the
   event remained pending, and the incident was not duplicated.
5. A temporary HTTPS receiver was exposed only to the API container's fixed
   private source and required an `Idempotency-Key`.
6. One dispatcher delivered the unavailable event in one attempt.
7. Restoring the game service closed the same incident after 195 seconds.
8. The recovered event was delivered in one attempt with a different valid
   idempotency key.
9. The previous release-candidate image passed migration and readiness checks;
   the current image was then restored with durable counts unchanged.
10. The receiver override was removed and alert delivery was disabled again.

## Cause

The initiating cause was the deliberate operator stop used by the test plan.
The exercise therefore does not identify an organic infrastructure or software
root cause. It validates detection and recovery behavior under a known failure,
not why an unplanned outage might occur.

## What Worked

- Thresholded polling opened one incident without duplication.
- Incident state and its alert event committed atomically.
- Pending work survived an API restart while dispatch was disabled.
- Recovery closed the original incident rather than creating a second one.
- The receiver observed two distinct idempotency keys and one request per event.
- Rollback and roll-forward preserved durable state.
- Readiness recovered and no incomplete command or dead letter remained.

## Limits And Risks

- The receiver was temporary and inside the control-plane boundary. This is not
  evidence for a durable off-host paging integration.
- The exercise covered one controlled endpoint and one incident sequence.
- At-least-once behavior under ambiguous receiver failure was not exercised.
- The result does not prove high availability, long-term reliability, or an
  achieved SLO.
- Production delivery must remain disabled until a permanent receiver proves
  ordered, idempotent historical catch-up behavior.

## Follow-Up

Completed:

- bounded Reader fleet, incident, activity, delivery-status, and pending-work
  views now expose the safe operational evidence needed for investigation;
- the first complete `API-01` seven-day window was evaluated independently;
- stable releases use verified candidate digests rather than rebuilding.

Still required before delivery activation:

- implement the durable adapter selected after the direct Grafana Cloud IRM
  trial failed duplicate and recovery correlation, as recorded in
  `docs/v2.19-permanent-receiver-readiness.md`;
- prove deduplication by event ID and chronological handling of unavailable and
  recovered events;
- perform a separately reviewed one-dispatcher canary without deleting or
  reclassifying pending records.

## Evidence

- `docs/v2.3-production-deployment.md`, controlled recovery evidence.
- `docs/v2.3-readiness.md`, recovery and release-soak decision matrix.
- `docs/release-notes-v2.3.md`, published release claim.
- `docs/alert-delivery.md`, delivery and re-enablement contract.
- `docs/v2.18-readiness.md`, current pending-delivery Reader boundary.

Raw target evidence remains owner-only outside Git. This document contains only
the sanitized facts already recorded in the repository.
