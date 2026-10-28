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

Local and target checks are in progress. No recovery acceptance is claimed yet.
