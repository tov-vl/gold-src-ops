# Release timing - October 2026

## Baseline: v2.75

Read from GitHub Actions job/run timestamps on 2026-10-13. Wall time below is
`updatedAt - createdAt` for each completed run, including dispatch/queue time;
job time is `completedAt - startedAt`. This is elapsed time, not billed minutes.
The successful product revision is `05a32f9cfebd8f18fcd846ad722aa47093c09be9`;
main, candidate and stable use `73a099a1bc34140aebd43753bcf54525c61ecc74`.

| Stage | Run | Wall time | Quality | Container Smoke | Wait after addon compile before Container Smoke |
| --- | --- | --- | --- | --- | --- |
| Product PR | [38415593616](https://github.com/tov-vl/gold-src-ops/actions/runs/38415593616) | 11m16s | 3m45s | 7m10s | 3m10s |
| Main | [38417310527](https://github.com/tov-vl/gold-src-ops/actions/runs/38417310527) | 10m57s | 4m02s | 6m35s | 3m12s |
| Candidate | [38417398391](https://github.com/tov-vl/gold-src-ops/actions/runs/38417398391) | 15m10s | 4m10s | 6m45s | 3m13s |
| Stable | [38419942812](https://github.com/tov-vl/gold-src-ops/actions/runs/38419942812) | 3m10s | 11s | 7s | 8s |
| Closure PR | [38420701546](https://github.com/tov-vl/gold-src-ops/actions/runs/38420701546) | 47s | 19s | 10s | 16s |
| Closure main | [38420925486](https://github.com/tov-vl/gold-src-ops/actions/runs/38420925486) | 40s | 9s | 10s | 4s |

From creation of the successful product PR run at 14:41:55 UTC to completion of
closure main at 15:19:09 UTC, the elapsed span was 37m14s. This includes operator
work, rollout and acceptance, and overlapping main/candidate CI; do not sum the
run durations as release wall time. The earlier failed product run
[38413917808](https://github.com/tov-vl/gold-src-ops/actions/runs/38413917808)
lasted 11m32s and is excluded from the successful-path comparison. Its outdated
language assertion and repair are recorded in [v2.75 readiness](v2.75-readiness.md).

## Selected optimization

Run Container Smoke in parallel with Quality Gate, retaining its dependency on
Change Scope and Map Menu Compile. Their runners, checkouts, builds and test
containers are separate; no build artifact is consumed from Quality Gate.
The gap between addon completion and quality completion was 187-189 seconds in
all three successful full runs. This is the modeled critical-path saving when
job durations and runner availability are comparable. Observed results are below.

Existing required checks and all tests remain. Each of the three publication
jobs directly needs successful quality, container and browser jobs. GitHub's
[dependency semantics](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_idneeds)
and [implicit success condition](https://docs.github.com/en/actions/reference/workflows-and-actions/expressions#status-check-functions)
keep failed, skipped or cancelled prerequisites from enabling publication.
The routing smoke checks those dependencies and rejects status-function overrides
that could bypass the default success gate. Current main rules require Quality
Gate, Container Smoke and Browser Smoke independently; no repository rule changes
are part of this work.

Tradeoff: a bad revision can spend runner time on Container Smoke while Quality
Gate fails. Successful-run runner work is not reduced, only scheduled earlier.
Browser Smoke, publication, rollout and acceptance ordering are unchanged.

## Why candidate-result reuse was not selected

Candidate started at 14:54:23 UTC, while same-SHA main CI completed at 15:04:44.
A reuse check at candidate start would find no completed success and fall back
to full CI. Waiting for main might save runner work, but adds a separate evidence
lookup/trust boundary and its own wait. PR head and squash main also have different
SHAs. The bounded scheduling change removes a measured serial wait without any
new permission, API lookup, evidence age policy or skipped test.

## Validation and limits

Local routing smoke passed with all four modes (full, docs, addon, stable), scope
and addon failure propagation, and publication dependency checks. Six in-memory
mutations (removing quality or adding an always override in each publication job)
were rejected by the smoke. The full local .NET suite was not repeated for this
workflow-only change; required PR and main CI both passed the full path. The PR
executed 1025 ordinary tests and all 56 browser tests; no test was removed.

No application version, release tag, registry publication, production configuration
or deployment was needed for this CI-only change. The initial comparison used
full PR/main runs and did not establish an end-to-end release saving. The later
v2.76 candidate measurement is recorded below.

## Observed comparison

[PR #340](https://github.com/tov-vl/gold-src-ops/pull/340) was squash-merged at
`94241fde000b85ee2e3e302231969d1617dea912` after full CI. Main CI also passed.
No image was published; this change includes no production action or replacement
of the accepted v2.75 Web artifact.

| Run | Evidence | Wall time | Quality | Container Smoke | Dispatch after addon completion |
| --- | --- | --- | --- | --- | --- |
| PR before | [38415593616](https://github.com/tov-vl/gold-src-ops/actions/runs/38415593616) | 11m16s | 3m45s | 7m10s | 190s |
| PR after | [38422772454](https://github.com/tov-vl/gold-src-ops/actions/runs/38422772454) | 7m20s | 3m46s | 6m23s | 3s |
| Main before | [38417310527](https://github.com/tov-vl/gold-src-ops/actions/runs/38417310527) | 10m57s | 4m02s | 6m35s | 192s |
| Main after | [38423907954](https://github.com/tov-vl/gold-src-ops/actions/runs/38423907954) | 7m23s | 3m27s | 6m09s | 3s |

Observed PR wall time fell by 3m56s
(34.9%), and main by 3m34s
(32.6%). Container Smoke started
187s before Quality Gate ended in the PR and
152s before it ended on main. Previously it started
3s after Quality Gate in each baseline run. This confirms removal of the serial
wait. Container execution time and runner dispatch also varied; the entire wall
clock difference must not be attributed to scheduling alone.

These are one before/after pair per event on unchanged application code, not a
statistical performance guarantee. Candidate/stable publication and production
acceptance were not rerun for this CI-only change. Their gates and job dependencies
are unchanged. The later candidate-specific measurement is recorded below.

## First later candidate - v2.76, 2026-10-14

[Candidate run 38561442481](https://github.com/tov-vl/gold-src-ops/actions/runs/38561442481)
passed all eleven jobs at source `a563cead118a2c040622124835cec026c4d76219`.
Workflow wall time was 11m22s, including image publication and independent image
checks. Quality Gate took 3m59s and Container Smoke 6m24s. Container Smoke started
3s after addon compilation, 3m05s before Quality Gate completed. Publication began
only after all required prerequisites passed; independent API image verification
finished last.

This confirms the intended overlap on an actual candidate. It is not a controlled
before/after release comparison: application scope, runners and image verification
differ from the earlier release. No additional CI optimization or test removal is
justified by this single observation. Stable promotion remains a separate workflow.
