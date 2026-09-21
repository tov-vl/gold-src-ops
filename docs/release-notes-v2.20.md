# GoldSrcOps v2.20.0 Release Notes

Status as of 2026-09-21: release candidate preparation. Local implementation,
deployment contracts, container smoke, and recovery rehearsal are complete.
Product review, candidate publication, target acceptance, and stable promotion
remain pending.

## Overview

GoldSrcOps v2.20 adds a bounded Operator workflow for reviewing terminal
provider-delivery messages. It makes a dead letter inspectable and allows one
append-only review record without changing delivery truth or authorizing a
retry, replay, deletion, reclassification, or queue unblock.

The feature spans the server-side Web BFF and the independent AlertReceiver.
Its shared authorization remains file-backed and outside browser code. The
public receiver route remains restricted to availability-event ingestion.

## Included In v2.20

- Bounded internal list, detail, review, and immutable receipt endpoints for
  provider dead letters.
- A third additive AlertReceiver migration containing the append-only review
  relation, unique message constraint, and restrictive foreign key.
- Exact request-id idempotency, one-review-per-message enforcement, concurrent
  conflict handling, and preserved provider outbox state.
- Operator-only list, detail, confirmation, submission, and receipt pages with
  antiforgery and a ten-minute single-use confirmation.
- Server-side reconciliation after an uncertain transport result without an
  automatic retry.
- Separate disabled-by-default receiver and Web activation overlays using one
  owner-controlled authorization file.
- Cross-stack preflight and container coverage for private `401/200`, public
  `404`, migration reapplication, and encrypted backup/restore.
- A corrected standalone Caddy route order that retains the exact existing
  availability-event allowlist.

## Data And Security Boundary

Review responses expose bounded identifiers, action, attempts, failure
metadata, timestamps, and optional review evidence. They do not expose provider
envelopes, raw response bodies, database connections, provider credentials,
receiver ingress authorization, or the operations credential.

The browser receives neither receiver URL nor authorization. Web obtains the
credential from an owner-controlled file and sends it only to the private
receiver alias. Disabled endpoints remain hidden as `404`; enabled endpoints
reject missing or mismatched authorization as `401`. Production Caddy exposes
no provider-operations route.

## Compatibility And Rollback

- The control-plane database and public API contracts are unchanged.
- The receiver migration is additive, has no backfill, and can coexist with
  the accepted v2.19 receiver binary.
- The source sender and provider worker remain disabled throughout rollout and
  acceptance. Review does not activate either worker.
- Web and receiver operations overlays must be applied and removed as one
  reviewed boundary using the same secret file.
- Rollback restores the accepted v2.19 Web and AlertReceiver digests and removes
  both overlays. The additive table and any accepted review remain preserved;
  no down migration or row deletion is part of rollback.

## Acceptance

Repository acceptance requires protected-branch CI, independently verified
candidate digests, the focused authorization and review tests, browser role and
token-boundary checks, three receiver migrations, private/public route checks,
and encrypted backup with a `100%` repository check and isolated restore.

Target acceptance additionally requires a fresh receiver backup and restore
rehearsal, disabled-first Web/receiver rollout, exact shared-secret preflight,
one bounded review of the preserved v2.19 dead letter, immutable receipt
reconciliation, and three healthy continuity samples over at least three
minutes. Results will be recorded only after those gates pass.

## Known Limits

- Review is audit evidence, not remediation. It cannot retry, replay, delete,
  reclassify, or unblock provider work.
- The receiver remains co-located with the control plane and therefore does not
  provide a separate failure domain or high availability.
- Acceptance does not prove notification delivery, escalation, sustained
  provider operation, paging reliability, long-term reliability, or an SLO.
- Provider delivery and source alert delivery remain separate, disabled
  operational decisions.

## References

- [v2.20 release readiness](v2.20-readiness.md)
- [v2.20 provider delivery operations](v2.20-provider-delivery-operations.md)
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [PostgreSQL backup and restore](postgresql-backup.md)
- [Security](security.md)
- [Project backlog](backlog.md)
- [v2.19.0 release notes](release-notes-v2.19.md)
