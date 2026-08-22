# GoldSrcOps v1.1.0 Release Notes

Prepared: 2026-08-22. Status: release candidate; tag and GitHub Release not yet
published.

## Overview

GoldSrcOps v1.1 is a focused operability release. It packages the existing v1
API as a production-oriented .NET 10 container, makes that deployment shape a
required CI smoke test, and documents configuration, migration, rollout, probe,
and rollback responsibilities. It does not introduce a new API capability or
pull the deferred v2 outbox work forward.

## Included In v1.1

- Multi-stage .NET 10 image with an ASP.NET Core-only runtime layer.
- Non-root execution on container port `8080`.
- Exclusion of the SDK, EF tooling, Development settings, and ignored local
  settings from the runtime image.
- Separate, serialized EF Core migration action before application rollout.
- Hardened runtime contract with a read-only root filesystem, bounded `/tmp`
  tmpfs, no added capabilities, and `no-new-privileges`.
- Production configuration fail-fast verification.
- Isolated PostgreSQL-backed container smoke automation with finally-based
  resource cleanup.
- Required GitHub Actions `Container Smoke` after `Quality Gate`.
- Platform-neutral deployment guidance for image identity, secrets, topology,
  migrations, probes, rollout, and rollback.
- OpenTelemetry SDK and instrumentations `1.18.0` with the direct Prometheus
  exporter `1.18.0-beta.1`.

## Compatibility

- The v1 HTTP endpoints and authorization policies are unchanged.
- No database migration or domain-state transition is added by v1.1.
- The existing Development startup, demo, A2S, RCON, polling, incident,
  retention, health, and metrics behavior remains covered by the 141-test suite.
- Production migrations still run separately; ordinary API startup never
  applies them.
- The direct `/metrics` endpoint remains authenticated and retains its v1
  contract.

## Verified Candidate Baseline

The 2026-08-22 local release-candidate gate completed with:

- `141/141` tests passed;
- zero Release build warnings and errors;
- clean formatting verification;
- no vulnerable direct or transitive NuGet packages;
- successful production image inspection and configuration fail-fast check;
- successful separate migration application to PostgreSQL 16;
- `200 Healthy` liveness and database-backed readiness from the hardened API
  container.

Detailed evidence and the remaining publication checklist are recorded in
[docs/v1.1-readiness.md](v1.1-readiness.md).

## Deployment Notes

Build from the final signed release tag, publish an immutable image tag, and
deploy the registry digest. Do not rebuild an old source tag for rollback
because the Dockerfile follows servicing `10.0` base-image tags.

The repository does not yet publish an image. A release operator must provide
the external PostgreSQL database, OAuth 2.0 or OpenID Connect provider,
TLS-terminating proxy, secret injection, migration job, and image registry.

Use [docs/deployment.md](deployment.md) for the complete runtime and rollout
contract and [docs/smoke-test.md](smoke-test.md) for repository verification.

## Intentional Limits

- Exactly one active polling worker and one active snapshot-retention worker
  remain the supported v1 topology.
- Real RCON verification remains restricted to a server the operator owns.
- The direct Prometheus exporter is still prerelease. Architecture Decision 12
  records why v1.1 retains it and when to move to stable OTLP export.
- Provider-specific production manifests, automatic image publication,
  distributed polling leases, alert delivery, and an outbox are deferred.

## Release References

- Readiness evidence: [docs/v1.1-readiness.md](v1.1-readiness.md)
- Deployment contract: [docs/deployment.md](deployment.md)
- Architecture decisions: [docs/architecture-decisions.md](architecture-decisions.md)
- Security model: [docs/security.md](security.md)
- RCON operations: [docs/rcon.md](rcon.md)
- Snapshot retention: [docs/snapshot-retention.md](snapshot-retention.md)
- Full smoke test: [docs/smoke-test.md](smoke-test.md)
- v1.0.0 release notes: [docs/release-notes-v1.md](release-notes-v1.md)

## Publication Status

The `v1.1.0` tag and GitHub Release are intentionally pending. Publish only
after the final candidate pull request, post-merge `main` run, and signed-tag CI
all pass both `Quality Gate` and `Container Smoke`.
