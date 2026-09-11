# GoldSrcOps v2.5.0 Release Notes

Status as of 2026-09-11: signed candidate `v2.5.0-rc.1` is published and its API
and Web images are independently digest-verified. It has not been deployed, and
no stable v2.5 release has been published.

## Overview

GoldSrcOps v2.5 extends the authenticated Operator portal with six narrowly
guarded server-lifecycle workflows. It keeps the v2.4 public dashboard and
Reader investigation surface intact while adding explicit review and
confirmation steps for mutations that can change monitoring, connection
metadata, credentials, or the game-server process.

The six repository slices are integrated in protected `main` through pull
requests [#108](https://github.com/tov-vl/gold-src-ops/pull/108) through
[#113](https://github.com/tov-vl/gold-src-ops/pull/113). They are not part of
the signed `v2.4.0` application revision. Candidate publication is complete;
production verification remains pending.

The repository baseline after the signed `v2.4.0` tag also carries forward
operational maintenance completed on `main`: WireGuard-scoped SSH preflight
hardening, availability shadow-audit tooling and policy alignment, and v2.4
release and `API-01` evidence updates. These changes are part of the exact v2.5
candidate diff, but do not expand the six-workflow product scope below. The
completed candidate-head review classified every post-v2.4 commit and found no
undeclared application behavior.

## Candidate Publication

| Property | Candidate evidence |
| --- | --- |
| Candidate version | `v2.5.0-rc.1` |
| Source revision | `c8000c090cb0de03f848ec4e999bcf4505332b93` |
| API image digest | `sha256:ebbecc713e71e879f72234588274354efc8da9d08a266290e8bd1da8248c58da` |
| Web image digest | `sha256:b59313b35ed6c408b952f6f0a31b3311918cd2d10c28759b6ab17a8478c7191d` |
| Candidate workflow | [GitHub Actions run #34586962203](https://github.com/tov-vl/gold-src-ops/actions/runs/34586962203) |

The signed annotated tag targets the exact revision above. The successful tag
workflow built the API and Web images independently, verified both published
digests, and did not create a mutable stable alias or deploy the candidate.

## Included In v2.5

- Operator-only pause and resume controls for scheduled monitoring. Reader
  sessions can inspect the state but cannot discover or submit the mutation.
- Idempotent registration of an existing endpoint in `Paused` state. The Web
  host supplies a UUID idempotency key, and the API rejects reuse for a
  different registration intent.
- Optimistic-concurrency-protected editing of non-secret server connection and
  polling metadata while monitoring is paused.
- Alias-only RCON credential binding and rotation while monitoring is paused
  and no retained command is `Pending` or `Running`. Secret values and the
  current alias never cross the read boundary.
- A fixed restart workflow that queues only the existing `Restart` command
  after fresh command-state validation.
- A two-step map-change workflow with a shared strict map-name allowlist and a
  subject-, server-, and map-bound one-time confirmation.
- Consistent antiforgery validation, explicit acknowledgement, bounded
  server-side confirmations, submit-time precondition checks, durable command
  reconciliation, and no automatic retry after an uncertain outcome.

Raw RCON remains available only through the authenticated API and is not added
to the Web UI.

## API And Persistence Compatibility

The read contracts add server and credential revision values. JSON clients that
ignore unknown response properties remain compatible. Mutation clients must be
updated deliberately:

- `PATCH /api/servers/{id}` now requires a positive `ExpectedRevision` and can
  return a conflict when monitoring is active or the loaded revision is stale.
- `PUT /api/servers/{id}/credentials/rcon` now requires the expected server and
  credential revisions and can reject stale, racing, active-monitoring, or
  incomplete-command requests.
- `POST /api/servers` remains compatible without an idempotency header, while
  the v2.5 Web workflow always supplies a canonical UUID `Idempotency-Key` and
  explicitly requests a paused registration.

Three additive EF Core migrations add nullable registration-idempotency
metadata, a unique registration-request index, and non-null server and
credential revisions with a default value of `1`. The credential uniqueness
index is renamed without changing its columns. Existing rows require no
application backfill.

The candidate migration bundle must come from the exact API image being
deployed. Before production rollout, an isolated restore rehearsal must prove
both the forward migration and the previous v2.4 runtime against the migrated
schema. Routine application rollback keeps the additive schema in place; a
database restore is reserved for a separately diagnosed migration failure.

## Candidate Acceptance Boundary

Repository closure and immutable candidate publication are complete: the exact
candidate revision passed `Quality Gate`, `Container Smoke`, and `Browser
Smoke`, and the signed tag published and independently verified both images.

`v2.5.0-rc.1` is acceptable for production evaluation only after the remaining
gates pass:

1. A fresh encrypted off-host backup, repository check, isolated restore
   rehearsal, production preflight, and same-image migration action pass.
2. Public health, OIDC Reader/Operator/no-role boundaries, token-free browser
   responses, durable queues, and controlled-server continuity pass before any
   mutation is submitted.
3. Reversible lifecycle checks and deliberately approved disruptive command
   checks complete in the order defined by
   [v2.5 release readiness](v2.5-readiness.md).

The live registration form may be rendered and authorization-tested without
creating a disposable production record. Until the product has a reviewed
retirement workflow or another real server is intentionally onboarded,
database-backed integration tests remain the write-path evidence for
registration.

## Availability Boundary

The prospective `API-01` window remains independent of v2.5 delivery. Candidate
work does not pause, reset, backfill, or reinterpret the active window. Any bad
or missing primary minute during a rollout consumes the unchanged error budget.
The first complete review remains due after `2026-09-17T16:50:00Z`.

A v2.5 candidate or stable release does not claim an achieved SLO, high
availability, multi-region resilience, long-term reliability, or real-player
adoption.

## Known Limits

- The reference deployment remains a single control-plane node and one Web
  instance. A Web restart invalidates process-local authenticated sessions.
- Registration has no delete workflow; release verification must not create a
  throwaway production server record.
- The browser never receives bearer tokens, RCON secrets, secret references,
  raw RCON input, command payloads, or dead-letter payloads.
- PostgreSQL remains the command-serialization and optimistic-concurrency
  boundary. The Web host is not an execution authority.
- An uncertain HTTP or RCON outcome is reconciled through current server state
  and durable command history before any deliberate follow-up.
- No additional multi-day soak is required solely for these UI workflows. The
  bounded live acceptance requires healthy post-action polls and queue state;
  longer availability evidence continues through `API-01`.

## References

- [v2.5 release readiness](v2.5-readiness.md)
- [Project backlog](backlog.md)
- [RCON operations](rcon.md)
- [Security](security.md)
- [Reference production deployment](v2.3-production-deployment.md)
- [Production Compose contract](../ops/production/README.md)
- [v2.4.0 release notes](release-notes-v2.4.md)
