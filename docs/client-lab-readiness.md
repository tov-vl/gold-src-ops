# Client lab repository tooling readiness

## Boundary

The 2026-10-31 repository stage owns reusable source and offline checks only:
the nine-file Windows runner, controller templates, signed update protocol,
profile/run generators, disposable fixture lifecycle, queue maintenance planner
and runbook. No target installation, game launch, new access, cloud resource or
production/addon rollout is part of this stage. No application release tag or
stable promotion is required for this source-only delivery.

Future installation affects a privileged updater, worker and queue and must use
R2-level trust, predecessor, cleanup and rollback evidence. The current laptop
installation remains the accepted predecessor until that separate adoption is
reviewed and verified. Earlier real-client RU/EN results do not prove this new
generated bundle was installed or accepted.

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
Product CI is assessed on the PR revision independently of these local results.

Real-client acceptance is deliberately pending: adoption needs exact
generated hashes, the local trust-engine migration if applicable, source-built
helper validation, fresh RU/EN
frames, independent fixture markers and verified cleanup. No renderer, PvP or
24-hour human-activity claim follows from these policy checks.
