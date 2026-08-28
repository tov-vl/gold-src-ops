# GoldSrcOps v2.2.0 Release Notes

Prepared: 2026-08-28. Status: release candidate; not yet tagged or published.

## Overview

GoldSrcOps v2.2 hardens the existing GoldSrc RCON client so a command response
can be collected from one or more ordinary `A2A_PRINT` datagrams. Earlier
versions returned after the first command-response datagram, which could persist
a successful but incomplete summary when a server flushed longer console output
in several chunks.

This is a backward-compatible minor release over v2.1.0. Public HTTP,
authentication, authorization, persistence, alert-delivery, and container
contracts remain unchanged. The release adds bounded receive-drain behavior,
endpoint isolation, independently validated response limits, focused protocol
tests, and owned-server evidence.

## Included In v2.2

- Receive-order assembly of single or multi-datagram RCON `A2A_PRINT`
  responses with one final normalization pass.
- One cancellation-backed end-to-end deadline covering endpoint resolution,
  challenge exchange, command send, first response, and quiet draining.
- A connected IPv4 UDP socket so an unrelated endpoint cannot satisfy either
  receive operation.
- A configurable quiet interval plus independent datagram-count and aggregate
  wire-byte ceilings.
- Explicit protocol failure instead of known partial success when a receive
  ceiling or the overall deadline is reached while response data continues.
- Preserved cancellation, timeout, authentication, malformed-response, socket,
  and result-sanitization behavior.
- Synthetic UDP coverage for quiet completion, cancellation, endpoint
  isolation, malformed datagrams, both response ceilings, continuous flow, and
  challenge or first-response timeouts.
- Guarded `say` dispatch and read-only multi-datagram `cvarlist` verification
  against an isolated local ReHLDS 3.14.0.857 instance.

## Configuration

Existing deployments remain valid when the new settings are omitted. Explicit
values outside their ranges fail startup validation.

| Setting | Default | Valid range | Purpose |
| --- | ---: | ---: | --- |
| `Rcon:ResponseDrainMilliseconds` | `100` | `10` to `1000` | Complete after this quiet interval following at least one response. |
| `Rcon:MaxResponseDatagrams` | `32` | `1` to `256` | Bound command-response datagram count. |
| `Rcon:MaxResponseBytes` | `65536` | `5` to `1048576` | Bound aggregate wire bytes, including connectionless headers. |

`Rcon:TimeoutMilliseconds` remains the overall deadline.
`Rcon:MaxResponseLength` remains the smaller post-sanitization character limit
for the result summary persisted in command history.

## Compatibility And Rollback

- Existing single-datagram responses follow the same successful path.
- Public endpoints, DTOs, JWT policies, command types, metrics, and database
  schema are unchanged.
- No EF Core migration or data backfill is required.
- The command is never retried automatically because a timed-out request may
  already have executed remotely.
- Rolling back to v2.1.0 restores the one-datagram limitation without changing
  persisted data or configuration requirements.

## Verified Candidate Baseline

The 2026-08-28 local candidate gate completed with .NET SDK `10.0.400`, Docker
Engine `29.7.2`, and PostgreSQL `16-alpine`:

- `259/259` tests passed with zero skips;
- Release build completed with zero warnings and zero errors;
- formatting verification produced no changes;
- NuGet Audit and the vulnerable-package report found no known vulnerable
  direct or transitive package;
- the production container smoke applied every migration to a clean database
  and passed image hardening, configuration fail-fast, liveness, and readiness
  checks.

Design pull request #19 and implementation pull request #20 passed the required
`Quality Gate` and `Container Smoke`. Pull request #20 merged as implementation
revision `f6baf40`, and its genuine
[post-merge `main` run](https://github.com/tov-vl/gold-src-ops/actions/runs/33166531803)
passed both jobs on that exact SHA.

Detailed evidence and accepted boundaries are recorded in
[docs/v2.2-readiness.md](v2.2-readiness.md).

## Owned-server Evidence

The isolated ReHLDS flow exercised the normal guarded `say` path and a read-only
`cvarlist` response through the authenticated API and durable dispatcher. The
long response arrived as 12 ordinary `A2A_PRINT` datagrams totaling 16,549 wire
bytes. The maximum observed inter-datagram gap was 8.206 ms, below the retained
100 ms quiet interval. Capture metadata excluded payloads, credentials, bearer
tokens, and RCON passwords.

This is representative evidence for one controlled ReHLDS build. It cannot
prove completeness under packet loss, reordering, production load, different
network paths, or every Valve HLDS and third-party engine variant.

## Deployment Notes

No migration or coordinated schema rollout is needed. Deploy the production
image with the existing RCON secret-provider contract. Keep the new defaults or
choose validated values from measured owned-server behavior; increasing limits
also increases the maximum time or memory consumed by one command response.

Continue to use the guarded owned-server workflow. Start with `-WhatIf`, verify
the exact server identity, and never use a third-party server for RCON tests.
After an ambiguous timeout, inspect durable command state before deciding on a
manual retry.

## Intentional Limits

- Legacy RCON UDP responses have no fragment identity, count, order metadata,
  or explicit completion marker.
- The quiet interval is a heuristic. A later datagram can be missed after the
  collector has completed successfully.
- UDP loss and reordering cannot be detected or repaired by this protocol.
- Source-endpoint filtering does not prove response authenticity; the RCON
  credential and trusted network path remain the security boundary.
- Full console output is not retained. The existing bounded sanitized result
  summary remains the persistence contract.
- IPv6, quoted-password support, automatic retry, new command types, and a
  management UI remain deferred.

## Release References

- Readiness evidence: [docs/v2.2-readiness.md](v2.2-readiness.md)
- RCON response design: [docs/v2.2-rcon-response-reliability.md](v2.2-rcon-response-reliability.md)
- RCON operations: [docs/rcon.md](rcon.md)
- Deployment contract: [docs/deployment.md](deployment.md)
- Previous release: [docs/release-notes-v2.1.md](release-notes-v2.1.md)
- Full smoke test: [docs/smoke-test.md](smoke-test.md)

## Publication Status

`v2.2.0` is a prepared release candidate. Final candidate integration through
protected `main`, signed annotated tagging, tag-triggered verification, GitHub
Release publication, and the post-release documentation update remain.
