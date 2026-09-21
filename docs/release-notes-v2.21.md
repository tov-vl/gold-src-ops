# GoldSrcOps v2.21.0 Release Notes

Status as of 2026-09-21: released. Signed stable tag `v2.21.0` identifies the
exact accepted `v2.21.0-rc.1` revision, and the stable API, Web, and
AlertReceiver references preserve the independently verified candidate
digests.

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

Readiness PR [#200](https://github.com/tov-vl/gold-src-ops/pull/200) froze exact
revision `f9b794596b2642c83d44d9177c450d3458eb22ea`. Candidate
[workflow #35618048968](https://github.com/tov-vl/gold-src-ops/actions/runs/35618048968)
published and independently verified all three workflow images.

R1 target acceptance replaced only AlertReceiver and Web, in that order, with
their candidate digests. API, both PostgreSQL schemas, Caddy, telemetry,
identity configuration, and game-host runtime remained unchanged. Three
healthy samples spanned 181 seconds with exact image identity, zero unexpected
restarts, healthy public and private endpoints, unchanged durable queue and
review counts, and both delivery workers disabled. The authenticated Operator
overview matched bounded owner-only evidence and exposed no credential,
payload, raw provider response, or mutation control. Public provider-operations
access remained absent. Reader denial is supported by exact-revision policy and
browser tests; no separate live Reader session was created during target
acceptance.

Signed stable tag `v2.21.0` targets the exact accepted revision. Stable
[workflow #35624106092](https://github.com/tov-vl/gold-src-ops/actions/runs/35624106092)
skipped all three image-build steps, promoted the accepted API, Web, and
AlertReceiver digests unchanged, and independently smoke-tested each stable
reference. The [GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.21.0)
is published. Stable promotion changed no production runtime.

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
- [Release readiness pull request](https://github.com/tov-vl/gold-src-ops/pull/200)
- [Candidate workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35618048968)
- [Stable workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/35624106092)
- [GoldSrcOps v2.21.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.21.0)
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [Security](security.md)
- [Project backlog](backlog.md)
- [v2.20.0 release notes](release-notes-v2.20.md)
