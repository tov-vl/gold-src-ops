---
name: goldsrcops-delivery
description: Implement, verify, package, and release GoldSrcOps product slices using the repository architecture, risk gates, evidence records, and bundled approval model. Use for milestone development, release readiness, candidate publication, production acceptance, stable promotion, or release closure in this repository.
---

# GoldSrcOps Delivery

Deliver one bounded GoldSrcOps product or release slice without rediscovering
the repository workflow, disturbing concurrent work, or overstating evidence.

## Establish The Boundary

1. Read the repository `AGENTS.md` and run `git status --short --branch`.
2. Identify existing modified and untracked paths. Treat them as concurrent work
   unless the current request explicitly owns them; do not stage or commit them
   incidentally.
3. Define the requested outcome, affected runtime, compatibility boundary,
   rollback boundary, and evidence needed to call the work complete.
4. Read only the context relevant to the task:
   - product work: `docs/project-brief.md`, the current section of
     `docs/backlog.md`, `docs/architecture.md`, and applicable decisions;
   - operations work: the owning document such as `docs/alert-delivery.md`,
     `docs/postgresql-backup.md`, `docs/security.md`, or
     `docs/observability.md`;
   - release work: `docs/release-process.md`, `docs/deployment.md`, and the
     current readiness and release-notes documents.
5. Prefer current source, tests, migrations, contracts, and workflow definitions
   over prose when they disagree. Verify drift-prone external state live before
   relying on it.

## Implement A Product Slice

- Inspect the owning code and tests before editing. Follow existing dependency
  direction and local patterns rather than introducing a parallel architecture.
- Keep the change reviewable and within the stated slice. Do not fold unrelated
  cleanup or another milestone into it.
- Preserve authentication, secret, idempotency, timeout, cancellation,
  ordering, retry, and concurrency invariants.
- Add tests at the narrowest owning layer. Add PostgreSQL, container, or browser
  coverage when the changed contract actually crosses that boundary.
- For schema work, create a new migration, inspect its generated SQL, and record
  rollout and mixed-version implications. Do not edit an applied migration or
  let ordinary startup migrate a database.
- Keep tracked evidence sanitized. Store private infrastructure facts and raw
  production evidence only in the owner-controlled operator record.

## Verify Efficiently

Run focused tests while implementing. Use the complete local .NET Quality Gate
once when the revision is ready for review or when the changed boundary
warrants it:

```powershell
dotnet restore GoldSrcOps.sln -p:AuditPipeline=true
dotnet format GoldSrcOps.sln --verify-no-changes --verbosity minimal --no-restore
dotnet build GoldSrcOps.sln --no-restore
dotnet test GoldSrcOps.sln --no-build
dotnet list GoldSrcOps.sln package --vulnerable --include-transitive
```

Run these commands serially. Use the focused `tools/smoke/` or `ops/` rehearsal
owned by the changed subsystem when required. Do not repeat an unchanged full
suite merely to duplicate green required CI; investigate conflicting evidence
instead.

Before presenting the slice as ready:

- inspect the complete scoped diff;
- confirm no unrelated files are staged or included;
- confirm documentation describes only proved behavior;
- report every material check that was skipped or remains external.

## Close A Product Pull Request

Keep implementation, release notes, and pending readiness evidence in one PR
when practical. Push without force, wait for all required checks, and squash
merge only when GitHub reports the PR mergeable and every required check is
green. A documentation-only follow-up must remain documentation-only for the
repository's fail-closed CI fast path.

Remote actions require either an exact request or a frozen named release bundle
that includes them. A bundle described in `docs/release-process.md` is one
authorization boundary; do not interrupt it with repeated confirmation prompts
while version, revision, affected runtime, risk class, and action remain
unchanged.

## Run A Release

1. Read `docs/release-process.md` in full and classify the highest applicable
   risk as D0, R1, R2, or R3.
2. Freeze the version, reviewed `main` revision, affected runtime, compatibility
   claim, rollback inputs, evidence plan, and claim boundary.
3. Publish a signed annotated release-candidate tag from that exact revision.
   Require the complete tag workflow, independently verified API and Web
   digests, source revision, and OCI metadata.
4. Deploy only verified digests. Recreate only affected components and run the
   target evidence required by the risk class.
5. Accept or roll back at the first unexplained failure. Do not mutate durable
   state to make a check pass and do not retry an operation whose side effect is
   uncertain.
6. Promote the accepted candidate with a signed annotated stable tag at the
   same revision. Verify that stable references reuse the accepted digests
   without rebuilding and that the GitHub Release exists.
7. Close with one sanitized evidence PR that updates readiness, release notes,
   links, anomalies, and claim limits.

Use external wait time for non-mutating preparation, documentation, and
evidence drafting. Do not parallelize competing production mutations, tag
creation before identity freeze, or stable promotion before candidate
acceptance.

## Stop Conditions

Pause forward progress and obtain a new decision when:

- the frozen version, revision, affected runtime, risk class, or claim changes;
- force push, history rewrite, tag replacement, registry deletion, database
  restore, down migration, queue drain, manual command retry, or another
  unplanned mutation becomes necessary;
- an operation has an uncertain outcome and retry could duplicate a side
  effect;
- a new credential, identity, permission, provider configuration, or secret
  transport is needed;
- rollback cannot restore the recorded boundary.

Never request or expose a secret in chat. Use only the trusted local or provider
prompt that consumes it.

## Finish And Hand Off

Report:

- the bounded outcome and files changed;
- focused tests, complete local gates, CI, artifact, and production evidence as
  separate categories;
- remote actions actually performed;
- remaining gates, risks, and the single next action;
- current branch, HEAD, and dirty paths when another task will continue the
  work.

Do not claim a release or external integration is complete because local code,
one request, or provider documentation alone succeeded.
