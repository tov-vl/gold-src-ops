# GoldSrcOps Repository Guidance

## Scope

These instructions apply to the entire repository. Keep milestone-specific
scope, decisions, and evidence in the existing `docs/` records rather than
turning transient release state into permanent agent guidance.

## Start With Current State

- Run `git status --short --branch` before planning or editing.
- Treat every existing modified or untracked path as concurrent work unless the
  current request clearly owns it.
- Do not reset, clean, stash, revert, overwrite, stage, or commit unrelated
  changes. Never switch branches in a shared dirty checkout merely to simplify
  the task.
- Re-read a file immediately before patching it when another task may be
  editing the same area. Keep the patch narrow and review the final diff for
  accidental overlap.
- Prefer adding a new, clearly owned file over modifying an in-progress file
  when both approaches satisfy the request.

## Sources Of Truth

For repository behavior and intent, use this order when information disagrees:

1. Current source, tests, migrations, contracts, and CI workflows.
2. `docs/architecture.md`, `docs/architecture-decisions.md`, and the applicable
   operational runbook.
3. The current milestone section in `docs/backlog.md`, its readiness record,
   and its release notes.

For claims about GitHub, Grafana, Auth0, production runtime, or provider state,
freshly inspected external state is authoritative. Documentation can lag those
systems, so verify the relevant state before making a claim that depends on it.

## Architecture Boundaries

- Preserve the modular-monolith boundaries described in
  `docs/architecture.md`: Domain owns invariants, Application owns use-case
  orchestration and ports, Infrastructure owns EF Core and external transports,
  Contracts owns public HTTP shapes, and API/Web own their host concerns.
- `GoldSrcOps.Web` is a server-side OIDC BFF. Do not expose bearer tokens,
  credentials, secret references, or protected API access to browser code.
- Keep public and Reader projections bounded and sanitized. Do not add private
  server addresses, RCON data, webhook material, or raw provider responses to
  public contracts, logs, metrics, screenshots, or tracked evidence.
- Ordinary application startup must not apply database migrations. Create a new
  EF Core migration for required schema changes; never edit an already applied
  migration. Inspect generated SQL and document locking, backfill, mixed-version,
  rollback, and recovery implications.
- Keep delivery, command, polling, retention, and game-event workflows
  idempotent and bounded. An uncertain side effect is a reconciliation problem,
  not permission to retry automatically.

## Implementation

- Follow existing project patterns and keep changes inside the smallest
  relevant ownership boundary.
- Add tests at the layer where the behavior is owned. Include PostgreSQL or
  browser integration coverage only when the changed contract crosses those
  boundaries.
- Preserve cancellation, timeout, retry, ordering, and concurrency semantics;
  do not apply analyzer suggestions mechanically when those semantics change.
- Keep PowerShell compatible with PowerShell 7 and shell scripts compatible
  with their existing strict, fail-closed style.
- Do not add secrets, local paths, private topology, raw production payloads,
  or operator-only evidence to Git.

## Verification

Run the narrowest meaningful check first. Do not run competing .NET builds or
tests concurrently against shared `bin` and `obj` directories.

For the local .NET Quality Gate, use the same command sequence as the CI
`Quality Gate` job:

```powershell
dotnet restore GoldSrcOps.sln -p:AuditPipeline=true
dotnet format GoldSrcOps.sln --verify-no-changes --verbosity minimal --no-restore
dotnet build GoldSrcOps.sln --no-restore
dotnet test GoldSrcOps.sln --no-build
dotnet list GoldSrcOps.sln package --vulnerable --include-transitive
```

Do not repeat the complete suite on an unchanged revision solely to reproduce a
green required CI run. Use the focused tests and smoke scripts associated with
the affected boundary, and report checks that were not run.

## Documentation And Evidence

- Update `docs/backlog.md` for active milestone status,
  `docs/architecture-decisions.md` for durable decisions, and
  `docs/project-brief.md` only when the project-level description changes.
- Keep release-specific scope and results in the matching readiness and release
  notes documents. Do not turn a prospective observation window into a blocker
  for unrelated product development.
- Keep public evidence sanitized. Credentials, private host data, backup
  identifiers, raw database rows, and emergency procedures belong only in the
  owner-controlled operator record outside Git.
- Distinguish local verification, CI evidence, published-artifact evidence, and
  production acceptance. One does not imply another.

## Pull Requests And Releases

- Follow `docs/release-process.md` and `docs/deployment.md` for release work.
- Use the highest applicable D0/R1/R2/R3 risk class and run only the additional
  target evidence required by that class.
- A user-approved named release bundle may cover its ordinary push, PR, squash
  merge, signed annotated tags, immutable publication, reviewed rollout,
  bounded acceptance, stable promotion, and final evidence PR. Do not request
  repeated approval while the frozen version, revision, runtime scope, risk
  class, and operation remain inside that bundle.
- Stop for a new decision when scope changes, a destructive or uncertain retry
  is proposed, a new credential or permission is required, or rollback can no
  longer restore the recorded boundary.
- Never force-push, replace a published tag, overwrite an immutable artifact,
  rebuild during stable promotion, or include secrets in a command, chat,
  tracked file, log, or retained evidence.

## Completion Report

State what changed, which focused and broad checks passed, which checks were
not run, any external actions performed, and the next unresolved gate. Do not
describe a candidate, deployment, or release as complete without its required
independent evidence.
