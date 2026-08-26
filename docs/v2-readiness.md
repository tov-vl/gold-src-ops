# GoldSrcOps v2 Alert Delivery Readiness

Review date: 2026-08-26. Status: released as `v2.0.0`.

Outcome: all six implementation slices for transactional incident-alert
delivery are complete. The local and remote quality gates and production
container smoke pass. Pull request #8 was squash-merged into protected `main`
as a verified commit, and local `main` is synchronized. Version selection,
release notes, signed tagging, tag-triggered verification, and GitHub Release
publication are complete.

## Capability Evidence

| Capability | Implementation evidence | Verification evidence | Status |
| --- | --- | --- | --- |
| Transactional alert creation | Versioned unavailable/recovered contracts, explicit outbox writer and monitoring unit of work, unique event type per incident | Deterministic polling and PostgreSQL tests prove commit, rollback, and duplicate prevention | Ready |
| Durable PostgreSQL processing | Atomic due-message claims, claim IDs, attempt accounting, conditional completion, per-incident ordering, and expired-claim recovery | Concurrent PostgreSQL tests cover ownership, ordering, recovery, statistics, and retention | Ready |
| Bounded webhook transport | One POST per application attempt, stable `Idempotency-Key`, request timeout, redirect rejection, status classification, bounded `Retry-After`, and zero response-body reads | Synthetic Kestrel tests cover success, retryable/permanent responses, timeout, caller cancellation, redirect, headers, and an unconsumed response body | Ready |
| Hosted dispatcher | Scope per attempt, bounded concurrency, equal-jitter retry, direct dead-letter on permanent or exhausted delivery, and maintenance isolation | Unit tests cover delivered, retry, permanent/exhausted dead-letter, exception sanitization, retry bounds, recovery, statistics, and cleanup | Ready |
| Operations and telemetry | Sanitized structured logs, OpenTelemetry counters/histogram/gauges, bounded processed-row retention, and default-disabled validated configuration | Log-safety, Prometheus endpoint, options, registration, and PostgreSQL tests pass | Ready |
| Production deployment contract | HTTPS webhook requirement, secret injection guidance, multi-instance topology, rollout, recovery, rollback, and alert runbook | Container smoke rejects Production HTTP, starts the enabled dispatcher, and proves endpoint/authorization values are absent from logs | Ready |

## Local Verification

The candidate gate ran on 2026-08-26 with .NET SDK `10.0.400`, Docker Engine
`29.7.2`, and PostgreSQL `16-alpine` in the isolated container flow:

```powershell
dotnet restore GoldSrcOps.sln -p:AuditPipeline=true
dotnet format GoldSrcOps.sln --verify-no-changes --verbosity minimal --no-restore
dotnet build GoldSrcOps.sln --configuration Release --no-restore
dotnet test GoldSrcOps.sln --configuration Release --no-build --no-restore
dotnet list GoldSrcOps.sln package --vulnerable --include-transitive --no-restore
pwsh -NoProfile -File .\tools\smoke\container.ps1
```

Results:

- restore with NuGet Audit succeeded;
- formatting verification produced no changes;
- Release build completed with zero warnings and zero errors;
- all `199/199` tests passed with zero skips;
- no vulnerable direct or transitive NuGet package was reported;
- the production image ran as non-root and excluded the SDK plus Development
  and local settings;
- missing required Production configuration failed fast;
- an HTTP alert webhook failed Production startup with the expected validation;
- every migration, including alert outbox persistence, applied to a clean
  PostgreSQL database as a separate action;
- the hardened API container returned `200 Healthy` for liveness and readiness;
- the enabled alert dispatcher started without logging its HTTPS endpoint or
  synthetic authorization marker;
- temporary containers, the dedicated network, and the smoke image were
  removed.

## Publication Evidence

- Pull request [#8](https://github.com/tov-vl/gold-src-ops/pull/8) integrated
  the complete alert-delivery capability into protected `main`.
- The required `Quality Gate` and `Container Smoke` checks passed on candidate
  revision `8877da6` before merge.
- GitHub created verified squash commit `2f17aa8` with a valid signature.
- The post-merge
  [CI run](https://github.com/tov-vl/gold-src-ops/actions/runs/32956963549)
  passed both required jobs on `main`.
- Documentation closeout pull request
  [#9](https://github.com/tov-vl/gold-src-ops/pull/9) passed both required jobs
  and merged as verified commit `3d1b04f`; its post-merge workflow also passed.
- Local `main` was fast-forwarded to `3d1b04f`, and the merged local and remote
  implementation and documentation branches were deleted.
- Final candidate pull request
  [#10](https://github.com/tov-vl/gold-src-ops/pull/10) passed both required jobs
  and merged as verified release revision `9d7176f`; its
  [post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/32962056966)
  also passed.
- Signed annotated tag `v2.0.0` identifies `9d7176f`, and its
  [tag-triggered workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/32962599218)
  passed both required jobs.
- The stable
  [GoldSrcOps v2.0.0 GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.0.0)
  was published from that tag.

## Accepted Boundaries

- Delivery is at least once. The receiver must deduplicate by the stable event
  ID because an ambiguous HTTP outcome can be retried.
- The container smoke verifies production wiring but intentionally does not
  contact a real external webhook. Synthetic-server tests own that boundary.
- Target-environment webhook TLS, identity-provider metadata, database TLS,
  secret injection, probe routing, receiver capacity, and backup restoration
  still require deployment evidence.
- Dead-letter replay and an administrative replay API remain deferred. Manual
  recovery must preserve the original event ID and immutable payload.
- One configured webhook and one alert contract version are supported. Routing,
  subscriptions, provider-specific channels, and service extraction remain
  deferred.
- Polling remains a singleton responsibility even though alert dispatch is
  multi-instance safe.

## Publication Checklist

- [x] Push `feature/v2-alert-dispatcher` and open pull request #8 into `main`.
- [x] Pass required `Quality Gate` and `Container Smoke` checks on the final
  candidate SHA.
- [x] Merge through protected `main` and verify post-merge checks.
- [x] Fast-forward local `main` and verify the GitHub signature.
- [x] Select `v2.0.0` and prepare candidate release notes.
- [x] Integrate the final release candidate through protected `main` and verify
  post-merge checks.
- [x] Create and push the signed annotated `v2.0.0` tag, then verify
  tag-triggered checks.
- [x] Publish the GitHub Release and record final publication status.
