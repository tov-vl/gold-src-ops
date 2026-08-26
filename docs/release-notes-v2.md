# GoldSrcOps v2.0.0 Release Notes

Prepared: 2026-08-26. Published: 2026-08-26. Status: released.

## Overview

GoldSrcOps v2 adds reliable incident-alert delivery to the existing modular
monolith. Availability transitions now enqueue versioned events in the same
PostgreSQL transaction as incident state, and a hosted dispatcher delivers
them to one generic HTTPS webhook through a durable outbox.

The v1 HTTP endpoints and authorization policies remain compatible. This
release adds an application capability, an additive database migration, new
production configuration, and an asynchronous operational workflow.

## Included In v2

- Versioned `server.availability.unavailable` and
  `server.availability.recovered` event contracts.
- PostgreSQL outbox storage with database constraints, claim ownership,
  per-incident ordering, retry scheduling, stale-claim recovery, and bounded
  processed-row retention.
- Transactional enqueueing from polling so incident changes and alert events
  commit or roll back together.
- Generic HTTPS webhook delivery with a stable `Idempotency-Key`, bounded
  request timeout, redirect rejection, status classification, and bounded
  `Retry-After` handling.
- Hosted delivery dispatcher with configurable concurrency, exponential jitter,
  permanent-failure classification, retry exhaustion, and dead-letter state.
- OpenTelemetry counters, duration metrics, and backlog gauges for enqueueing,
  attempts, outcomes, recovery, cleanup, and queue health.
- Sanitized structured logs that exclude webhook endpoints, authorization
  values, event payloads, and response bodies.
- Deployment and operations guidance for configuration, secrets, topology,
  rollout, recovery, dead letters, retention, and rollback.

## Reliability And Delivery Semantics

- Delivery is at least once. Receivers must deduplicate by the stable event ID.
- Each application attempt sends at most one HTTP request. Ambiguous transport
  outcomes may be retried with the same event ID and idempotency key.
- PostgreSQL atomically claims due messages and conditionally completes or
  reschedules only the active claim.
- Messages for one incident remain ordered while independent incidents can be
  dispatched concurrently.
- Permanent outcomes and exhausted retry budgets move directly to dead letter.
- Expired claims are recovered without bypassing the configured attempt limit.
- Processed-row cleanup deletes one bounded oldest batch per maintenance pass.

## Compatibility And Migration

- Existing v1 REST endpoints, JWT bearer authentication, and `Reader` and
  `Operator` policies are unchanged.
- The outbox migration is additive. Apply migrations as a separate serialized
  deployment action before rolling out the v2 application image.
- Alert delivery is disabled by default and requires an HTTPS webhook in
  Production when enabled.
- Alert dispatch is safe across multiple application instances. Polling and
  snapshot-retention singleton constraints remain unchanged.
- The existing v1.1 production container and health-probe contracts remain in
  effect.

## Verified Release Baseline

The 2026-08-26 local candidate gate completed with .NET SDK `10.0.400`, Docker
Engine `29.7.2`, and PostgreSQL `16-alpine`:

- `199/199` tests passed with zero skips;
- Release build completed with zero warnings and zero errors;
- formatting verification produced no changes;
- NuGet Audit and the vulnerable-package report found no known vulnerable
  direct or transitive package;
- all migrations applied to a clean PostgreSQL database as a separate action;
- the production container smoke passed hardening, fail-fast configuration,
  liveness, readiness, migration, and alert-dispatch registration checks.

Implementation pull request #8, documentation pull request #9, final candidate
pull request #10, the post-merge `main` run, and the signed-tag run all passed
`Quality Gate` and `Container Smoke`. The signed release revision is `9d7176f`.

Detailed evidence and accepted boundaries are recorded in
[docs/v2-readiness.md](v2-readiness.md).

## Deployment Notes

Apply the outbox migration before starting v2 instances. Configure alert
delivery through external production configuration and secret injection; do
not commit webhook authorization material. Start with delivery disabled,
validate receiver capacity and TLS in the target environment, then enable the
dispatcher and monitor backlog, retries, dead letters, and oldest-message age.

Build the application image from the final signed release tag and deploy an
immutable registry digest. Use [docs/deployment.md](deployment.md) for the
container contract and [docs/alert-delivery.md](alert-delivery.md) for the
complete rollout and recovery runbook.

## Intentional Limits

- v2 supports one configured generic webhook and one alert payload version.
- Dead-letter inspection and replay remain operational procedures; there is no
  administrative replay API yet.
- The container smoke does not contact a real external webhook. Target TLS,
  identity, receiver capacity, and secret injection require deployment
  evidence.
- Polling still runs as a singleton responsibility.
- Provider-specific channels, subscriptions, a broker, service extraction,
  distributed polling leases, and automatic image publication remain deferred.

## Release References

- Readiness evidence: [docs/v2-readiness.md](v2-readiness.md)
- Outbox design: [docs/v2-alert-outbox.md](v2-alert-outbox.md)
- Alert delivery operations: [docs/alert-delivery.md](alert-delivery.md)
- Deployment contract: [docs/deployment.md](deployment.md)
- Architecture: [docs/architecture.md](architecture.md)
- Architecture decisions: [docs/architecture-decisions.md](architecture-decisions.md)
- Security model: [docs/security.md](security.md)
- Full smoke test: [docs/smoke-test.md](smoke-test.md)
- Previous release: [docs/release-notes-v1.1.md](release-notes-v1.1.md)

## Publication Status

Published on 2026-08-26 as the
[GoldSrcOps v2.0.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.0.0)
from signed annotated tag `v2.0.0`. Final candidate pull request #10, the
post-merge `main` run, and the tag-triggered run all passed `Quality Gate` and
`Container Smoke` on release revision `9d7176f`.
