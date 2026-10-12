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

Local routing smoke and documentation validation, full PR CI and post-merge
CI are required. New tag publication and external availability operations are
not exercised; their runner selections are reviewed against the successful
baseline above. No release, deployment or production restart is required.
