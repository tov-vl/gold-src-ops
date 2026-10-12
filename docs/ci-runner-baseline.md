# CI runner baseline

## Scope

CI maintenance only: replace 15 Linux `ubuntu-latest` selections in five
workflows with `ubuntu-24.04`, including the conditional lightweight addon
confirmation. Keep real addon compilation on its existing Windows runner.
Update the existing routing smoke assertion to match the explicit Linux label.
Triggers, permissions, job conditions, publication and recovery logic are unchanged.

GitHub announced an automatic `ubuntu-latest` migration starting 2026-10-19:
[runner image announcement](https://github.com/actions/runner-images/issues/14748).
The explicit label prevents that distribution switch. Hosted image and tool
updates within Ubuntu 24.04 remain possible; this is not a reproducible image pin.

## Verified previous runs

Runner setup logs inspected on 2026-10-12:

| Workflow / job | Successful run | Reported image / OS |
| --- | --- | --- |
| CI / Quality Gate and Publish Web Image | [38238021362](https://github.com/tov-vl/gold-src-ops/actions/runs/38238021362) | ubuntu-24.04 / 24.04.5 |
| Availability Shadow Audit | [35260170511](https://github.com/tov-vl/gold-src-ops/actions/runs/35260170511) | ubuntu-24.04 / 24.04.5 |
| Availability Evidence | [38213381062](https://github.com/tov-vl/gold-src-ops/actions/runs/38213381062) | ubuntu-24.04 / 24.04.5 |
| Availability Evidence Recovery | [33968872991](https://github.com/tov-vl/gold-src-ops/actions/runs/33968872991) | ubuntu-24.04 / 24.04.4 |
| Availability DNS Proof | [33964036998](https://github.com/tov-vl/gold-src-ops/actions/runs/33964036998) | ubuntu-24.04 / 24.04.4 |

The older manual workflow runs establish their previous image family, not a
new execution of their external operations. No availability workflow is
manually dispatched as part of this change.

## Verification boundary

Local routing smoke and documentation validation passed; full PR CI and
post-merge CI passed after the CSS assertion follow-up.
New tag publication and external availability operations are
not exercised; their runner selections are reviewed against the successful
baseline above. No release, deployment or production restart is required.

## Initial post-merge anomaly

[PR #326 CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38250301112)
passed all required checks. Initial
[main CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38251527181)
reported one browser failure: the player-guide navigation test immediately read
`text-decoration-line` as `underline` instead of `none` after navigation; 53
other browser cases passed. Both runs reported Ubuntu 24.04.5 and hosted image
20261004.327.1. This does not establish an OS difference as the cause.

A narrow test follow-up uses Playwright's bounded `ToHaveCSSAsync` assertion
for the same expected style, allowing the browser to apply CSS after navigation.
No timeout increase, test retry, weakened expectation or application change is
introduced. The timing race is the working explanation, not a reproduced root
cause. The follow-up PR and post-merge CI passed; acceptance is complete.

## Accepted evidence

Accepted on 2026-10-12 through [PR #326](https://github.com/tov-vl/gold-src-ops/pull/326).

- [Full PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38250301112) passed.
- [CSS assertion follow-up PR #327](https://github.com/tov-vl/gold-src-ops/pull/327) and [its full CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38252690196) passed.
- [Post-merge CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38253948590) passed at revision 2c4ffd072c2aefe44323d28778cb984a6c941b8c.
- Runner setup logs independently confirmed ubuntu-24.04 for Linux checks and
  the unchanged Windows runner for actual addon compilation.
- Local CI release fast-path smoke passed; documentation validation checked
  175 Markdown files and 982 local links before publication.
- The narrow CSS assertion follow-up passed a focused build, format check and
  five local runs of the four-viewport scenario.
- The local .NET suite was not repeated for this change; full .NET,
  addon, browser and container gates ran in CI.
