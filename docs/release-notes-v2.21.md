# GoldSrcOps v2.21.0 Release Notes

Status as of 2026-09-21: release-candidate preparation. The product revision is
integrated into protected `main`; candidate publication, target acceptance,
stable promotion, and final release evidence remain pending.

## Overview

GoldSrcOps v2.21 adds Provider Delivery Overview for the independent receiver
outbox. An authenticated Operator can see whether provider delivery is enabled,
the current pending, processing, dead-letter, and review workload, and the
oldest pending time without direct database or provider access.

This is an additive read-only Web and AlertReceiver release. It changes no
database schema, delivery worker, retry behavior, public route, authorization
role, identity, provider configuration, event producer, or game-host component.

## Included In v2.21

- Separately authorized `GET /internal/v1/provider-delivery/status`.
- Bounded worker configuration, queue counts, review workload, oldest-pending
  time, and observation time.
- Server-side Web composition that keeps receiver authorization outside browser
  code.
- Operator-only `/operator/provider-delivery` with clear, active,
  paused-with-work, and action-required states.
- Direct navigation to the existing immutable provider dead-letter review
  workflow.
- Explicit read-only copy and no controls for retry, replay, deletion,
  reclassification, unblocking, review submission, or worker activation.

## Data And Security Boundary

The endpoint and page expose aggregate queue state and timestamps only. They do
not expose provider envelopes, event payloads, raw responses, failure details,
claim identities, destinations, server addresses, credentials, tokens,
authorization values, or database rows.

The browser receives neither the receiver URL nor its authorization. The
endpoint reuses the disabled-by-default provider-operations boundary introduced
in v2.20, and production Caddy exposes no provider-operations route. Reader and
anonymous sessions cannot open the Operator overview.

## Compatibility And Rollback

- Existing routes and response contracts are unchanged; the internal status
  route and DTO are additive.
- No migration, backfill, queue drain, backup/restore rehearsal, or previous
  runtime schema rehearsal is required.
- Deploy AlertReceiver before Web to avoid a temporary unavailable overview.
  The v2.20 Web ignores the new receiver endpoint.
- Provider delivery and source alert delivery remain disabled operational
  decisions outside this release.
- Rollback restores the accepted v2.20 Web and AlertReceiver digests without a
  database, queue, authorization, API, Caddy, telemetry, identity, or game-host
  change.

## Acceptance

Repository and release evidence is recorded in
[v2.21 release readiness](v2.21-readiness.md). Product PR
[#199](https://github.com/tov-vl/gold-src-ops/pull/199) passed `Change Scope`,
`Quality Gate`, `Container Smoke`, and `Browser Smoke` before squash merge.

Target acceptance remains pending. It requires exact candidate Web and
AlertReceiver digests, receiver-before-Web rollout, three healthy read-only
samples over at least three minutes, an existing Operator session, matching
bounded owner-only queue evidence, forbidden Reader access, unchanged public
Caddy exposure, unchanged durable queue/review state, and both delivery workers
remaining disabled.

No production data should be created or changed to make the overview display a
particular state.

## Known Limits

- This is a current snapshot, not queue history, throughput analysis, or an
  event stream.
- The page does not auto-refresh.
- Worker configuration does not prove provider reachability or successful
  delivery.
- A short production smoke cannot prove long-term reliability, high
  availability, sustained provider operation, or an achieved SLO.
- The receiver remains co-located with the control plane and is not a separate
  failure domain.

## References

- [v2.21 release readiness](v2.21-readiness.md)
- [Provider Delivery Overview pull request](https://github.com/tov-vl/gold-src-ops/pull/199)
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [Security](security.md)
- [Project backlog](backlog.md)
- [v2.20.0 release notes](release-notes-v2.20.md)
