# Client lab repository tooling readiness

## Boundary

The 2026-10-31 repository stage owns reusable source and offline checks only:
the nine-file Windows runner, controller templates, signed update protocol,
profile/run generators, disposable fixture lifecycle, queue maintenance planner
and runbook. No target installation, game launch, new access, cloud resource or
production/addon rollout is part of this stage. No application release tag or
stable promotion is required for this source-only delivery.

The separate adoption below affects the existing privileged updater, worker and
queue and uses R2-level trust, predecessor, cleanup and rollback evidence.
Earlier real-client RU/EN results were not reused as proof of this bundle's
installation or acceptance.

## Local verification

- Profile, output-path and archive checks reject malformed identities, public
  addresses, path/command injection, duplicate jobs, invalid cleanup and changed
  activation/runner inputs before run output is created.
- PowerShell 7 and Windows PowerShell 5.1 passed 102 protocol checks, 24
  autonomous cases, six workflow cases, four worker-cleanup cases, 23 shell-launch
  checks, six focus projections, five queue-inventory checks and 60 updater checks
  each. All process/task/provider effects were mocked.
- The C# 5/.NET Framework 4.8 harness passed 422 checks: 129 bridge, 24 synthetic
  frame/focus, 244 focus diagnostics and 25 window-pattern checks. Native bridge
  compilation passed in Windows PowerShell 5.1 and PowerShell 7. No real input,
  focus, capture or game process was used. Scoped whitespace validation passed.
- Seven Python tests cover fixture/target/image/path refusal, permanent claims,
  foreign-container protection, cleanup after evidence failure and bounded tunnel
  framing. The CI path-selection smoke and changed-documentation checks passed.
- The fixture helper rebuilds reproducibly with the pinned Windows AMXX compiler
  and `-d0 -E`; its exact SHA-256 is checked locally and by the compiler CI job.
  A fresh EN fixture plan was rendered from the locally retained hash-pinned
  addon/dictionary and source-built helper with synthetic machine configuration. No container,
  relay or remote client was started by preparation.

### Static analysis

InspectCode 2026.2.3.1 completed against the current native harness project and
all five C# source files, at Hint and higher: zero errors, zero warnings and 61
notes. No severity was lowered and no suppression was added. Intentional notes:

| Rule | Count | Reason retained |
| --- | --- | --- |
| CA1031 | 1 | A bounded background provider transfers its exception to the caller; an unhandled provider failure must not terminate the worker |
| CA1515 | 4 | Public types are called dynamically by PowerShell outside the compiled assembly |
| MA0043 | 14 | `nameof` requires C# 6; the target compilation contract is C# 5 |
| MA0048 | 34 | The deployed native bridge is one pinned compilation unit; related inert test doubles share their harness files |
| MA0007 | 7 | Trailing-comma suggestions conflict with the existing formatter style; scoped formatting passes without them |
| UnusedMember.Global | 1 | PowerShell dynamically constructs the native game-window type |

The harness pins the build-only .NET Framework reference-assembly package;
see [Microsoft's build guidance](https://learn.microsoft.com/dotnet/framework/migration-guide/reference-assemblies).
It does not install or replace the target runtime.

## Solution quality and adoption gate

The complete local solution quality gate passed: audited restore, formatting,
build, tests and vulnerable-package listing. Build reported zero warnings/errors;
1294 tests passed and 62 opt-in browser tests were skipped. No vulnerable package
was reported. The separate native harness also passed with warnings as errors.
Product PR [#422](https://github.com/tov-vl/gold-src-ops/pull/422) was accepted.
Its [required CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41210422529)
and [post-merge CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41211598289)
passed all six verification jobs at the reviewed source revision.

## Existing laptop adoption - 2026-10-31

Accepted source: `de0920cf9b7c2426124535efd13025373a073bb3` from PR #422.
Only the existing client lab was changed; production, Selectel, identities,
keys, access rules and task definitions were preserved. No application release
tag, addon rollout or stable promotion was required.

- One local administrator migration installed the reviewed updater engine and
  its trusted hash. The predecessor runner, sequence, policy and task definitions
  were preserved, and a fresh successful SYSTEM task tick was verified. The
  installer passed 22 inert success/refusal/rollback checks in each of PowerShell
  7 and Windows PowerShell 5.1 before use. Protected rollback inputs are retained
  privately; rollback was not needed.
- A signed runner update advanced sequence 9 to 10. All nine installed hashes
  matched the reviewed payload. The four pinned dependencies retained their
  predecessor bytes. One pinned common script differed from a new render only
  in byte formatting; normalized text equality and its predecessor hash were
  checked before preserving those exact bytes in the payload manifest.
- Signed maintenance advanced sequence 10 to 11 and archived exactly one
  completed autonomous request. The result remained unchanged and its image
  remained readable. The updater ended enabled and unblocked at revision 16.
- Fresh EN and RU fixtures used the exact accepted addon 0.38.0/dictionary and
  source-built helper. Each language had one client dispatch and five native
  1920x1200 frames. EN completed at 17:14 UTC and RU at 17:19 UTC. All ten frames
  were reviewed: readable rules, both OK closes, alternate-alias reopening and
  returned movement passed without manual window selection or global input.
- Independent server markers confirmed both aliases, cooldown and movement
  after both closes. Each fixture reported zero AMXX error files. Temporary
  configuration restoration, final game-process absence, fixture/network removal
  and relay termination were independently checked. Final worker state was ready.

The raw worker/collector results intentionally remain unreviewed with
`acceptancePassed=false`; separate operator review records hold acceptance.
Profiles, native frames, logs, signed payloads and rollback records remain outside
Git. No renderer, PvP or 24-hour human-activity acceptance is claimed. Other
panels still have capture-only support; the autonomous interaction acceptance is
limited to the reviewed `/rules` profile.
