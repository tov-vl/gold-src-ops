# Shared network recovery

## Problem and correction

After a host interruption, dynamically addressed co-located services can take
the API's static addresses before it starts. The shared-edge receiver and the
Web telemetry attachment now require explicit static addresses. Production
preflight includes Web in telemetry uniqueness/subnet checks. The combined
receiver preflight rejects a receiver address outside the production edge
subnet or shared with API, Web or Caddy. Standalone receiver settings remain
independent.

Docker CLI service warnings are suppressed at the CLI log level during JSON
rendering; nonzero exit codes and configuration validation still fail closed.
Otherwise new CLI warnings can make valid JSON output unparseable.

The two new non-secret environment settings must be provisioned before using
the updated Compose files. Existing deployments without these settings refuse
to render. This is an R3 infrastructure correction; no image rebuild, migration,
queue replay, volume deletion, new port, credential or permission is required.

## Validation and recovery boundary

`tools/smoke/shared-network-addresses.ps1` verifies valid configuration, duplicate,
wrong-subnet and missing addresses for both attachments, plus standalone receiver.
It uses placeholder environments and does not start containers.

Target recovery preserves existing image identities and all durable volumes,
backs up exact configuration, updates the two Compose files and two environment
settings, recreates only Web and receiver, then starts the previous API and
Prometheus. A nonempty pending/running command queue stops API startup.

Acceptance requires at least three read-only samples across three minutes,
healthy services, route/auth boundaries, A2S, command/alert continuity, telemetry
and unchanged dependencies. Controlled Web/receiver restarts verify address
stability. Host reboot and Docker daemon restart are not part of this exercise.

Rollback restores the saved files and previous images for affected services;
the original baseline was already degraded and restoring it is not a healthy
API claim. Queue history and retention changes are not rolled back with SQL.
Private commands, topology, backups and evidence remain outside Git.

## Evidence

The correction was merged through [PR #404](https://github.com/tov-vl/gold-src-ops/pull/404)
at `a9af2b4bdd07e9b52f2e2de1825b8c42bf8533be`. All required PR checks passed,
including the new address cases in Container Smoke. Local validation passed
seven positive/negative address cases, standalone receiver compatibility,
PowerShell parser checks and a live read-only rendered-model comparison proving
that only the two approved addresses change.

Target recovery completed on 2026-10-28. Exact configuration backups were saved
before the four approved file changes. Only Web and receiver were recreated,
then the previous API and Prometheus containers were started. All application
image identities and mounts were preserved. Database, proxy, Collector and
Grafana container identities and restart counts were unchanged.

Four successful read-only samples spanned 244 seconds, including a controlled
restart of receiver and Web. Static addresses survived that restart. API
liveness/readiness, public status and Web health/home returned 200, anonymous
protected API returned 401, and the receiver's unsupported GET returned 404.
Both Prometheus scrape targets were up. A2S observations remained fresh and
reachable; pending/running command counts stayed zero. Existing backup evidence
was fresh and its timer remained active. Source/provider delivery stayed
disabled and receiver mode remained CatchUp. No rollback was required.

This exercise restores the previous application versions. It neither installs
v2.94 nor proves host-reboot independence. Interactive authenticated Reader
acceptance was not performed; the anonymous authorization boundary was checked.
The v2.94 rollout can now obtain a healthy rollback baseline.
