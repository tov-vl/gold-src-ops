# GoldSrcOps v2.25.0 Release Notes

Status as of 2026-09-22: remediation pending. `v2.25.0-rc.1` was published,
failed the external A2S gate, and was fully rolled back. A reviewed
`v2.25.0-rc.2` candidate remains pending.

## Overview

GoldSrcOps v2.25 gives a visitor one direct path from the anonymous public
status page to the accepted Counter-Strike 1.6 server. The page presents a
deliberately configured public endpoint together with current A2S state, map,
occupancy, observation freshness, a Steam deep link, and a copyable console
command.

The API and Web behavior is additive and read-only. The complete release also
adds a reversible game-host firewall policy that publishes only IPv4 game UDP,
preserves exact-source SSH and ReHLDS RCON, and does not change the database,
identity, worker, queue, managed profile, autostart policy, or game process.

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
- Plan-first public game firewall enable, verification, and exact rollback.
- Hash binding to the accepted profile, guarded-autostart marker, public
  configuration, and exact ReHLDS RCON source without recording the address.

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
- AlertReceiver, PostgreSQL, Caddy, telemetry, identity, workers, and external
  monitoring remain unchanged.
- Rollback restores the accepted v2.22 API and Web digests and deletes only the
  owned public IPv4 UFW rule. It does not restart the game or alter durable data.

The candidate source also contains the already accepted v2.23 managed-profile
and v2.24 guarded-autostart repository artifacts. The v2.25 firewall workflow
verifies their identities but does not reapply or replace them.

## Verification

Product PR [#213](https://github.com/tov-vl/gold-src-ops/pull/213) passed the
complete required CI in workflow
[#35747517220](https://github.com/tov-vl/gold-src-ops/actions/runs/35747517220),
and post-merge workflow
[#35748824441](https://github.com/tov-vl/gold-src-ops/actions/runs/35748824441)
passed on exact revision `38f275b880da67f94927b70bbc2b1ede746d884d`.

Candidate `v2.25.0-rc.1` publication and digest verification passed, and its
API/Web rollout produced three healthy samples. External A2S then timed out
because the original UFW policy still limited game UDP to the control plane.
API and Web were restored to the accepted v2.22 digests; the game process and
durable state were unchanged. Candidate `v2.25.0-rc.2`, the R3 firewall
rehearsal, operator connection, stable promotion, and final evidence closure
remain pending. The exact plan and claim limits are defined in
[v2.25 release readiness](v2.25-readiness.md).

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
- [Public Game Boundary](v2.25-public-game-boundary.md)
- [Product pull request](https://github.com/tov-vl/gold-src-ops/pull/213)
- [Release process](release-process.md)
- [Deployment](deployment.md)
- [Security](security.md)
- [v2.22.0 release notes](release-notes-v2.22.md)
