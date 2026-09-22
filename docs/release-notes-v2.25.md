# GoldSrcOps v2.25.0 Release Notes

Status as of 2026-09-22: release candidate pending. Product implementation is
merged and verified; candidate publication and target acceptance have not yet
occurred.

## Overview

GoldSrcOps v2.25 gives a visitor one direct path from the anonymous public
status page to the accepted Counter-Strike 1.6 server. The page presents a
deliberately configured public endpoint together with current A2S state, map,
occupancy, observation freshness, a Steam deep link, and a copyable console
command.

This is an additive read-only API and Web release. It changes no database
schema, authorization role, identity, worker, queue, retry behavior, game-host
file, managed profile, autostart policy, or server process.

## Included In v2.25

- Anonymous `GET /api/public/server` with a bounded response contract.
- Selection of one existing enabled inventory server by configured ID.
- Deployment-owned public display name, host, and game port.
- Normalized `online`, `offline`, and `unknown` states with stale online data
  mapped to `unknown`.
- Current map, player count, capacity, and last observation time.
- Responsive public join section with a Steam deep link and copyable
  `connect host:port` command.
- Fail-closed validation for partial or unsafe configuration.
- Omission of the join section when the feature is unconfigured or the selected
  inventory server is absent or disabled.

## Data And Security Boundary

The public response contains only the configured public name, host, and port,
normalized state, current map, player counts, and observation time. It does not
contain the inventory name, inventory host or query port, notes, RCON metadata,
provider identifiers, raw responses, incident details, command history,
credentials, or secret references.

The browser receives no bearer token, inventory projection, protected API
surface, or mutation control. The public endpoint is deliberately supplied by
deployment configuration instead of inferred from private inventory.

## Compatibility And Rollback

- No migration, backfill, data correction, or queue operation is required.
- API and Web deploy together from verified immutable candidate digests.
- The feature is absent when all four settings are absent; partial or unsafe
  configuration fails startup.
- AlertReceiver, PostgreSQL, Caddy, telemetry, identity, workers, external
  monitoring, and game host remain unchanged.
- Rollback restores the accepted v2.22 API and Web digests and owner-only
  environment without a database or game-host action.

The candidate source also contains the already accepted v2.23 managed-profile
and v2.24 guarded-autostart repository artifacts. The v2.25 rollout does not
reapply or rehearse those game-host changes.

## Verification

Product PR [#213](https://github.com/tov-vl/gold-src-ops/pull/213) passed the
complete required CI in workflow
[#35747517220](https://github.com/tov-vl/gold-src-ops/actions/runs/35747517220),
and post-merge workflow
[#35748824441](https://github.com/tov-vl/gold-src-ops/actions/runs/35748824441)
passed on exact revision `38f275b880da67f94927b70bbc2b1ede746d884d`.

Candidate publication, digest-pinned rollout, public endpoint verification,
one operator connection, three-minute continuity sampling, stable promotion,
and final evidence closure remain pending. The exact plan and claim limits are
defined in [v2.25 release readiness](v2.25-readiness.md).

## Known Limits

- The page advertises one server, not a searchable public server directory.
- The data refreshes with the existing A2S polling cadence; it is not a live
  socket feed.
- `unknown` can represent no observation, stale online data, or an unavailable
  current state.
- A successful Steam deep link can still be affected by local Steam client
  association or client network policy.
- Short acceptance cannot prove sustained traffic, multi-player gameplay,
  regional latency, long-term availability, high availability, or an SLO.

## References

- [v2.25 release readiness](v2.25-readiness.md)
- [Public Server Join](v2.25-public-server-join.md)
- [Product pull request](https://github.com/tov-vl/gold-src-ops/pull/213)
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [Security](security.md)
- [v2.22.0 release notes](release-notes-v2.22.md)
