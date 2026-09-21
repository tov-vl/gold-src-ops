# Release Process Runbook

This is the version-neutral operator runbook for taking a reviewed GoldSrcOps
change from a product pull request to a stable release. It defines the order of
work, the approval boundary, and the evidence that must survive the release.
Technical container, migration, and rollback contracts remain in
[Deployment](deployment.md). Each release keeps its version-specific scope and
results in its readiness and release-notes documents.

## Goals

- keep immutable artifact identity and rollback safety;
- match verification effort to the change's failure modes;
- avoid repeating equivalent tests after a revision has already passed them;
- minimize serial waits and interactive operator steps;
- keep the repository record useful without publishing private infrastructure
  or credentials.

This runbook does not weaken required branch protection, CI, digest
verification, migration safety, authorization checks, or secret handling.

## Public And Private Records

The canonical process belongs in Git. Keep these records tracked:

- release stages, gates, stop conditions, and rollback rules;
- sanitized commands and links to repository scripts or workflows;
- release scope, compatibility claims, and known limits;
- source revision, public tag names, immutable image digests, workflow and pull
  request links, and sanitized acceptance results;
- deviations that changed the release outcome or future procedure.

Keep the operator supplement outside Git with owner-only access. It may contain:

- credentials, tokens, private keys, recovery codes, or secret values;
- host addresses, provider identifiers, account identifiers, and private
  topology details;
- exact secret locations, emergency access instructions, and local aliases;
- raw production payloads, database rows, logs, backup identifiers, and
  unsanitized evidence;
- temporary commands or diagnostic artifacts that are useful only for one
  operator session.

Repository documents may describe how to obtain a value without recording the
value itself. Do not rely on obscurity for safety: public process documentation
is acceptable, but authentication material and private runtime data are not.

## Release Inputs

Freeze these inputs before publication work starts:

| Input | Required record |
| --- | --- |
| Release version | Stable and release-candidate tag names |
| Candidate source | Exact reviewed commit reachable from `main` |
| Change class | Highest applicable class from the risk table below |
| Affected runtime | API, Web, AlertReceiver, database, workers, game host, or infrastructure |
| Compatibility | Database, API, authentication, and mixed-version constraints |
| Rollback | Previous known-good digests for every affected runtime plus any separate recovery plan |
| Evidence plan | Required repository and target-environment checks |
| Claim boundary | What the release proves and what remains unverified |

Changing the version, candidate source, runtime scope, or compatibility claim
after this point creates a new release plan and invalidates the prior approval
bundle.

## Risk Classification

Use the highest applicable class. Uncertainty selects the broader class. The
detailed gate matrix is maintained in [Deployment](deployment.md#risk-based-release-gates).

| Class | Typical change | Target-environment evidence |
| --- | --- | --- |
| D0 | Documentation only | No production action |
| R1 | Additive read-only API or Web behavior | Digest-pinned rollout and a short read-only production smoke |
| R2 | State-changing behavior, authentication, worker, queue, or schema change | R1 plus the focused authorization, migration, queue, command, or worker rehearsal |
| R3 | Infrastructure, recovery, or SLO-policy change | Dedicated recovery, continuity, or policy exercise |

A long soak or prospective SLO window is independent evidence. It blocks a
claim that depends on that window, not unrelated product development or a
release whose claim boundary does not depend on it.

## Approval Bundle

One explicit approval for a named release and frozen scope may cover the
following standard sequence:

1. ordinary branch push without force;
2. pull request creation against `main`;
3. squash merge after required checks pass and GitHub reports the pull request
   mergeable;
4. creation, local verification, and push of signed annotated release-candidate
   and stable tags;
5. publication and verification of every immutable image in the frozen runtime scope;
6. the reviewed digest-pinned rollout and bounded target checks;
7. rollback of only the affected components to the recorded known-good digests
   when a documented gate fails;
8. creation and squash merge of the final evidence pull request after its
   required checks pass.

Do not ask for another confirmation while the action remains inside that exact
bundle. Stop and obtain a new decision when any of these conditions appears:

- the version, candidate commit, affected runtime, or change class changes;
- a force push, history rewrite, tag replacement, registry deletion, or other
  destructive operation becomes necessary;
- a database restore, down migration, data correction, queue drain, manual
  command retry, or unplanned production mutation is proposed;
- an operation has an uncertain outcome and repeating it could duplicate a
  side effect;
- a new credential, identity, permission, provider configuration, or secret
  transport is required;
- the rollback plan cannot restore the recorded boundary.

Reuse a valid authenticated session. Request interactive login only when the
session is absent or expired, and request a secret only through the trusted
local or provider prompt that consumes it. Never request a secret in chat.

## Ordered Release Flow

### 1. Close The Product Revision

1. Keep implementation, release notes, and the pending readiness record in one
   product pull request when practical.
2. Run focused local tests while developing. Let the required pull request jobs
   provide the single complete quality, container, and browser pass.
3. Review the final diff and classify the release risk.
4. Push normally, create the pull request, and wait for every required check.
5. Squash merge only when the checks are green and the pull request is
   mergeable.
6. Do not repeat the same complete local suite on an unchanged tree merely to
   reproduce green CI. Investigate any disagreement instead.

When a separate readiness-only pull request is unavoidable, keep it
documentation-only so the fail-closed documentation fast path can validate it.

### 2. Freeze Candidate Identity

1. Record the exact `main` revision and the commit and file inventory since the
   preceding stable tag.
2. Confirm the release-candidate and stable tags are unused and satisfy the tag
   contract in [Deployment](deployment.md#publish-and-version-the-image).
3. Complete the compatibility, rollback, evidence, and claim-boundary inputs.
4. Retain the previous production digests and private rollback data in the
   owner-only operator record.

Do not tag a moving branch or an unreviewed commit.

### 3. Publish The Candidate

1. Create and locally verify the signed annotated release-candidate tag at the
   frozen revision, then push that tag.
2. Require the tag workflow to run the full repository gate and publish every
   image in the frozen runtime scope. For v2.19 and later this includes the
   AlertReceiver image when that runtime is affected.
3. Require independent published-image smoke jobs and verify the source
   revision, OCI metadata, and immutable digest for each image.
4. Record the candidate identity in the sanitized release record and the
   rollback-sensitive details in the private operator record.

The candidate tag does not create a stable GitHub Release or a mutable
`latest` reference.

### 4. Roll Out By Digest

1. Capture a sanitized pre-rollout baseline for the applicable public health,
   container identities and restart counts, durable state, backup freshness,
   and dependent runtime continuity.
2. Run only the preflight and focused rehearsal required by the classified
   risk. An R1 release does not require a fresh restore rehearsal, game-host
   activation, or unrelated recovery exercise.
3. Deploy the verified candidate digests and recreate only the affected
   components.
4. Do not reconfigure or restart an unaffected database, proxy, telemetry
   service, game host, identity provider, or worker merely to complete the
   release.

Database and other state-changing operations follow their dedicated runbooks
and are never implied by an R1 application rollout.

### 5. Accept Or Roll Back

For an R1 release, collect at least three healthy read-only samples spanning at
least three minutes. Verify:

- public API liveness and readiness plus Web health;
- candidate version, source revision, and exact digests for every affected
  runtime image;
- no unexpected restart of changed or unchanged runtime components;
- the changed read surface through an existing authorized session;
- the relevant durable-queue, incident, command, A2S, bot, backup, and
  dependency invariants from the release evidence plan;
- no credential, token, private payload, or unauthorized mutation control is
  exposed.

R2 and R3 releases add their focused rehearsal without repeating unrelated
historical exercises. Stop on the first unexplained failure. Preserve evidence
and use the recorded rollback plan; do not mutate durable state to make a check
pass.

### 6. Promote The Accepted Candidate

1. Confirm production retained the candidate and every required acceptance gate
   passed.
2. Create and verify the signed annotated stable tag at the exact candidate
   revision, then push it.
3. Require stable publication to promote every accepted candidate digest
   without rebuilding.
4. Verify every stable reference resolves to its accepted digest,
   published-image smoke passes, and the GitHub Release is present.

Never move or replace a published tag. Correct a released artifact with a new
patch version.

### 7. Close The Repository Record

After stable publication, use one short evidence pull request to:

- replace pending readiness results with sanitized candidate, rollout,
  acceptance, and stable-publication evidence;
- update release notes and the README release pointer where applicable;
- link the product pull request, required workflows, and GitHub Release;
- record anomalies, rollback attempts, and claim limits without including
  private operator data.

Run the required documentation checks and squash merge the evidence pull
request. A separate pull request for each intermediate release stage is not the
default.

## Parallel Work

Use unavoidable wait time without crossing release gates:

- while product CI runs, draft readiness and release notes;
- while post-merge checks run, prepare the candidate inventory and private
  rollback record;
- while candidate images publish, prepare read-only preflight and acceptance
  commands without changing production;
- while production samples accumulate, draft the sanitized evidence section;
- while stable promotion runs, finish the combined evidence pull request.

Do not parallelize competing production mutations, stable publication before
candidate acceptance, or a tag operation before candidate identity is frozen.

## Time Budgets

These are diagnostic targets, not safety gates or reliability claims. CI queue
time, provider incidents, and a genuinely required interactive login are
reported separately.

| Stage | R1 target after product development is complete |
| --- | --- |
| Product PR review, CI, and merge | 20-40 minutes |
| Candidate freeze and tag | 5-10 minutes of operator work |
| Candidate publication | One CI run, normally no manual wait between jobs |
| Digest rollout and short acceptance | 10-20 minutes, including the three-minute sample interval |
| Stable promotion | One promotion CI run that reuses candidate gates, promotes without rebuild, and smoke-tests the stable digests |
| Combined evidence PR | 10-20 minutes |

The working target for an uncomplicated R1 release is 60-90 minutes after the
product revision is ready, excluding external queue or outage time. If a stage
exceeds its budget, identify the actual blocker instead of adding repeated
checks or confirmation loops. R2 and R3 releases use budgets appropriate to
their explicit rehearsal or recovery scope.

A push of the canonical stable tag `vX.Y.Z` selects the stable-promotion CI
path. Required check names remain present, but the complete quality, container,
and browser suites are not repeated for the unchanged candidate revision.
Candidate tags and manual publication recovery remain on the full path. The
publication jobs still fail closed unless every stable reference can promote a
matching candidate digest and pass its independent published-image smoke.

## Failure Rules

- A failed repository, publication, identity, preflight, health,
  authorization, or continuity gate stops forward progress.
- Roll back only through the reviewed path and only to recorded immutable
  artifacts. Application rollback and database recovery are separate
  decisions.
- Do not automatically retry an operation with an uncertain side effect.
- Preserve failed artifact identity and sanitized evidence for diagnosis.
- Keep long-running monitoring independent unless its result is an explicit
  claim gate for this release.

## Completion Checklist

- [ ] Product revision is reviewed and merged through protected `main`.
- [ ] Version, risk class, compatibility, rollback, and claim boundary are
      frozen.
- [ ] Signed candidate tag and every affected image digest are independently
      verified.
- [ ] Applicable production preflight and acceptance checks pass.
- [ ] Stable tag promotes the exact accepted digests without rebuilding.
- [ ] GitHub Release and published-image smoke are complete.
- [ ] One sanitized evidence pull request is merged.
- [ ] Private evidence remains owner-only and no secret entered Git history.

## Related Runbooks

- [Deployment](deployment.md)
- [Smoke test](smoke-test.md)
- [PostgreSQL backup and restore](postgresql-backup.md)
- [Security](security.md)
- [Observability](observability.md)
