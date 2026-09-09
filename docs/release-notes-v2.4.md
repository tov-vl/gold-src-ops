# GoldSrcOps v2.4.0 Release Notes

Prepared: 2026-09-09. Status: release candidate deployed; stable publication
pending.

## Overview

GoldSrcOps v2.4 adds a compact production Web experience and a reproducible
external availability-measurement path to the v2.3 reference deployment. The
public surface presents sanitized current status and bounded A2S history. The
authenticated portal supports Reader investigation and narrowly guarded
Operator actions without exposing bearer tokens, RCON secrets, raw command
payloads, or alert-event payloads to the browser.

The latest signed candidate is `v2.4.0-rc.9` from revision
`2769d8961985db1da3d00037013aafb542e3cb8b`. Its API and Web images were
published, digest-verified, deployed, and checked against the production
authorization and continuity boundaries. Stable v2.4 remains pending the final
fresh availability shadow audit and activation record.

## Included In v2.4

- A separate .NET 10 Blazor Web App packaged as an independently
  digest-addressed, non-root production image behind the shared Caddy edge.
- A public static-SSR dashboard with cached aggregate status and explicit
  unknown states, without server names, addresses, or provider identifiers.
- Bounded 24-hour and seven-day A2S history with fixed buckets, explicit gaps,
  and aggregate-only reachability values.
- A confidential OIDC BFF using authorization code with PKCE, an opaque secure
  session cookie, a bounded server-side ticket store, and persistent protected
  Data Protection keys.
- Reader views for server inventory, current status, open incidents, bounded
  observation and incident history, command audit history, and cursor-paged
  dead-letter inspection.
- An Operator-only `say` workflow with antiforgery validation, explicit
  acknowledgement, subject-bound one-time confirmation, audit, and no blind
  retry after an uncertain outcome.
- An Operator-only individual dead-letter replay workflow with durable API
  idempotency, concurrency protection, one-time browser confirmation, and a
  Reader-visible receipt.
- A provider-independent public availability contract with one canonical
  external readiness probe and separate diagnostic probes.
- A normalized expected-slot evaluator, create-only JSONL segments, scheduled
  private B2 archival, digest-verified read-only recovery, and sanitized audit
  output.
- Bounded transport-failure classification and an independently exercised
  alert route with three-bad/two-good hysteresis.

## Candidate Evidence

| Property | Candidate evidence |
| --- | --- |
| Source revision | `2769d8961985db1da3d00037013aafb542e3cb8b` |
| API image digest | `sha256:cad281cf5760dacc7757dbcc5e09ff81ddca4607539e24d69cb6c7f653a05a70` |
| Web image digest | `sha256:362bfce6a2025d1a9cec4845ab392515482650200e887dd4049c9870614b5fd5` |
| Candidate workflow | [GitHub Actions run #34235947471](https://github.com/tov-vl/gold-src-ops/actions/runs/34235947471) |
| Production result | API and Web health, OIDC boundary, runtime continuity, controlled-server state, durable queues, and backup freshness passed |

## Availability And SLO Boundary

`API-01` remains `Draft`. The final activation gate is the fresh 24-hour shadow
window ending at `2026-09-10T15:00:00Z`, evaluated only after its five-minute
maturity grace period. A passing result starts a new official denominator; no
shadow or v2.3 soak sample is imported.

Decision 23 changes the first objective to at least 99.5% good primary minutes
over seven rolling days, with 10,080 expected slots and at most 50 bad minutes.
Stable v2.4 publication may follow successful activation and does not imply
that this prospective seven-day objective has already been achieved.

## Compatibility And Limits

- Existing authenticated API policies remain the final authorization boundary;
  Web authorization does not bypass bearer validation.
- The public endpoints are additive and deliberately sanitized.
- The Web ticket store remains process-local for the single-instance MVP; a Web
  restart signs users out, and horizontal scaling remains unsupported.
- Raw RCON, restart, map-change, server configuration, and registration remain
  outside the Web UI.
- An uncertain RCON result is never retried automatically. Dead-letter replay
  relies on the existing durable API idempotency and concurrency contract.
- The reference deployment remains single-node and makes no high-availability,
  multi-region, long-term reliability, or real-player adoption claim.

## Stable Publication Gate

Before publishing `v2.4.0`:

1. Complete the recorded shadow window and pass the revised-policy audit.
2. Record the immutable `API-01` activation tuple and seven-day objective start.
3. Replace the pending status in these release notes and
   [v2.4 readiness](v2.4-readiness.md) with sanitized final evidence.
4. Pass the repository checks and promote the verified candidate API and Web
   artifacts through the signed stable-tag workflow.
5. Verify the published images and publish the GitHub Release.

## References

- [v2.4 readiness](v2.4-readiness.md)
- [Project backlog](backlog.md)
- [Reader portal](v2.4-reader-portal.md)
- [Public dashboard deployment](v2.4-public-dashboard-deployment.md)
- [External availability monitoring](v2.4-external-availability-monitoring.md)
- [Availability evidence exporter](v2.4-availability-evidence-exporter.md)
