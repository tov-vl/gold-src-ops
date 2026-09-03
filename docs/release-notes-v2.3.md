# GoldSrcOps v2.3.0 Release Notes

Prepared: 2026-09-03. Published: pending. Status: release candidate validated;
repository checks and stable publication pending.

## Overview

GoldSrcOps v2.3 turns the production-oriented backend from v2.2 into a
continuously operated reference deployment across a real external GoldSrc
network boundary. It combines one digest-pinned control plane with one
separately hosted ReHLDS and ReGameDLL_CS server, public HTTPS, external OIDC,
encrypted off-host PostgreSQL recovery, private OpenTelemetry collection, and
repeatable host and runtime operations.

This is a backward-compatible operational release over v2.2.0. Existing HTTP
and database contracts remain intact. The application adds optional OTLP metric
export and stricter production identity and reverse-proxy configuration, while
the larger change is the reviewed deployment, recovery, observability, and
evidence surface around it.

The release is not published yet. The accepted 24-hour soak completed after
24.095 hours with passing target evidence on both hosts and a passing bounded
address-stability comparison. This remains short release evidence, not a
long-term reliability or achieved-SLO claim.

## Included In v2.3

- Immutable stable and release-candidate image publication through GHCR, with
  digest verification and no mutable `latest` alias.
- A single-node production Compose topology for GoldSrcOps, PostgreSQL, Caddy,
  OpenTelemetry Collector, Prometheus, and Grafana.
- Same-source migration execution before API rollout, fail-closed production
  preflight, digest-pinned deployment, and documented rollback.
- Two-phase host hardening, restricted SSH, UFW and listener inspection, time
  synchronization, disk and memory checks, bounded container logs, and Docker
  boot recovery.
- Public DNS and trusted TLS termination through Caddy, with the API trusting
  only the reviewed reverse-proxy boundary.
- External OIDC authentication with explicit Reader and Operator policies,
  namespaced role claims, lifetime validation, and positive and negative live
  authorization checks.
- Client-side encrypted PostgreSQL backups in an off-host S3-compatible restic
  repository, scheduled retention preview, freshness checks, and an isolated
  restore rehearsal.
- A provider-independent game-host foundation and pinned ReHLDS
  `3.15.0.896` plus ReGameDLL_CS `5.30.0.814` installation and activation
  workflow.
- Production A2S polling and one guarded audited RCON `say` across the external
  boundary, followed by controlled restart and configuration restore evidence.
- Optional OTLP metrics export from the API through a private Collector into
  Prometheus and a provisioned Grafana operations dashboard.
- A controlled failure and recovery exercise covering incident transitions,
  durable alert work, API restart, idempotent webhook delivery, image rollback,
  and roll-forward.
- Fail-closed control-plane and game-host release-soak evaluators, deterministic
  smoke coverage, a readiness matrix, and an initial prospective SLO register.

## Reference Topology

The reference environment intentionally keeps the game server and control
plane on separate hosts. GoldSrcOps reaches the controlled server through the
same external A2S and RCON boundary that a provider-independent deployment must
support. PostgreSQL and telemetry services remain private to the control-plane
host; only Caddy publishes the HTTPS edge.

The deployment is pinned by registry digest. Database migrations run from the
same application image before the API starts. Backup credentials, restic key,
RCON password, OIDC credentials, host addresses, and provider identifiers
remain outside Git and outside public evidence.

## Candidate Baseline

The running candidate is the signed annotated tag `v2.3.0-rc.5`:

| Property | Candidate |
| --- | --- |
| Source revision | `58a74dafecb7eefe3b4f1e310347dbafb94d4b05` |
| API image digest | `sha256:f146c61e5eba942fc40d27792088ec6666fb2605959429093930c30a43f7d639` |
| Publication workflow | [GitHub Actions run #204](https://github.com/tov-vl/gold-src-ops/actions/runs/33631016313) |
| Rollback candidate | `v2.3.0-rc.4`, retained by immutable digest outside Git |
| Final operational evidence revision | Pending final `main` revision; recorded separately from the image source |

The final evaluator rejects any drift in version, source revision, image
digest, migration revision, required container identity, container start time,
restart count, or health state.

## Completed Target Evidence

- The control-plane host passed hardening, reboot, runtime listener, and
  external dependency checks.
- PostgreSQL backup, full repository check, isolated restore, migration
  verification, and idempotent migration rerun passed.
- Public liveness and database-backed readiness return HTTP `200` through a
  trusted TLS certificate.
- Reader, Operator, missing-role, expired-token, wrong-issuer, and
  wrong-audience authorization cases passed through the public route.
- The controlled game host passed pinned runtime installation, process and
  listener ownership, external A2S, source-restricted RCON, guarded command,
  restart, restore, and short post-restart observation checks.
- Collector, Prometheus, and Grafana passed private-network, target-health,
  metric-query, provisioning, and restart-count checks.
- The controlled recovery exercise opened and closed one threshold-qualified
  incident, preserved pending alert work across API restart, delivered both
  events once, and preserved durable state through rollback and roll-forward.
- The terminal release soak passed all 18 control-plane and eight game-host
  checks. All 1,444 persisted A2S attempts succeeded, no bot-positive poll was
  observed, required processes did not restart, and durable queues ended empty.

Detailed sanitized conclusions and the boundary around owner-only evidence are
recorded in [v2.3 readiness](v2.3-readiness.md).

## Final Release Gate

The operational evidence gates are complete. Repository checks and stable
publication deliberately remain separate:

| Gate | Status | Completion evidence |
| --- | --- | --- |
| Complete 24-hour interval | Passed | Control-plane evidence reports 24.095 hours, `Passed`, `TargetEvidence: true`, and 18/18 checks. |
| Control-plane continuity | Passed | Candidate and all six containers matched the baseline; terminal HTTPS, backup, telemetry, durable state, and capacity passed. |
| Game-host continuity | Passed | Eight of eight checks and every process/listener continuity flag passed. |
| A2S and bot observation | Passed | All 1,444 persisted polls succeeded, coverage recomputed to 99.9308%, maximum gap was 60.58 seconds, and bot-positive polls remained zero. |
| Trial address stability | Passed | The owner-only bounded comparison matched; only the sanitized result enters Git. |
| Initial SLI record | Complete | Aggregate terminal values are recorded without activating or claiming achievement of an SLO. |
| Final repository checks | Pending | Release-documentation pull request and post-merge `Quality Gate` and `Container Smoke` pass. |
| Stable publication | Pending | Signed `v2.3.0` tag promotes the verified candidate digest, the published digest smoke passes, and the GitHub Release is published. |

## Stable Artifact Publication

After the final release gate passes, signed annotated tag `v2.3.0` will target
the same source revision as `v2.3.0-rc.5`. This lets the publication workflow
promote the already verified candidate digest instead of building a new image
that did not participate in the soak.

Deployment hardening outside the API image, operational evidence, evaluators,
smoke coverage, and documentation added after the candidate remain on `main`
and are linked from the GitHub Release. They do not change the API image
inputs. The stable release record must state both the tagged application
revision and the later operational-evidence revision; it must not imply that a
new image was built from the latter.

## Compatibility And Rollback

- No v2.3 EF Core migration or data backfill is introduced over v2.2.0.
- Existing API routes and persisted domain contracts remain compatible.
- OTLP export is optional outside the reference production configuration and
  validates its endpoint, protocol, interval, and timeout when enabled.
- Production bearer configuration requires the reviewed authority, audience,
  role-claim type, and HTTPS reverse-proxy boundary.
- Rollback uses the retained `v2.3.0-rc.4` digest and compatible schema. It does
  not run an automatic down migration or replay an ambiguous RCON command.
- PostgreSQL recovery restores into an isolated target first; destructive
  in-place restore is not part of the automated baseline.

## Intentional Limits

- The reference deployment is single-node and does not claim high
  availability, zero-downtime deployment, multi-region failover, or Kubernetes
  readiness.
- The 24-hour observation is bounded release evidence, not proof of long-term
  reliability and not an achieved monthly SLO.
- Public API health has sampled external checks but no continuous independent
  black-box time series, so a public availability SLO remains inactive.
- One local VPN routing interruption affected the monitoring path while an
  independent external health probe remained successful. It was not classified
  as a production outage, but it reinforces the sampled-evidence boundary.
- The controlled server has no real-player adoption claim. YaPB and other bots
  remain absent during the release soak.
- GoldSrcOps does not provision provider infrastructure, manage billing, or
  replace a game-hosting control panel.
- Legacy RCON remains plaintext UDP with protocol-level ambiguity. Source
  restriction, a high-entropy secret, guarded commands, durable audit, and no
  blind retry remain mandatory.
- A compact public dashboard, authenticated operator UI, gameplay-event agent,
  entitlement flow, and payments remain future milestones.

## Release References

- Final readiness record: [docs/v2.3-readiness.md](v2.3-readiness.md)
- Initial SLI and SLO proposal: [docs/service-level-objectives.md](service-level-objectives.md)
- Production deployment plan: [docs/v2.3-production-deployment.md](v2.3-production-deployment.md)
- Controlled game-server baseline: [docs/v2.3-controlled-gameserver-baseline.md](v2.3-controlled-gameserver-baseline.md)
- Production operations: [ops/production/README.md](../ops/production/README.md)
- Game-host operations: [ops/gameserver/README.md](../ops/gameserver/README.md)
- Observability: [docs/observability.md](observability.md)
- PostgreSQL recovery: [docs/postgresql-backup.md](postgresql-backup.md)
- Previous release: [docs/release-notes-v2.2.md](release-notes-v2.2.md)

## Publication Status

Not published. Target evaluators, address stability, and the terminal SLI record
are complete. Replace this section only after release-documentation checks, the
signed stable tag, tag-triggered image verification, and GitHub Release
publication have all completed.
