# Availability workflow validation - 2026-10-14

## Observed failure

Four auxiliary workflow runs failed during the initial push of
`de3b71e7569a6975880ed75c34648f2f437fac88` on 2026-10-13, 15:31:11-14 UTC.
Each run has zero jobs. The public run annotation identifies the same error:
`The key 'deployment' is not allowed`.

| Workflow | Run | Rejected location |
| --- | --- | --- |
| Availability DNS Proof | [38422760761](https://github.com/tov-vl/gold-src-ops/actions/runs/38422760761) | `availability-dns-proof.yml:31:7` |
| Availability Evidence Recovery | [38422762784](https://github.com/tov-vl/gold-src-ops/actions/runs/38422762784) | `availability-evidence-recovery.yml:38:7` |
| Availability Evidence | [38422764913](https://github.com/tov-vl/gold-src-ops/actions/runs/38422764913) | `availability-evidence.yml:47:7` |
| Availability Shadow Audit | [38422767261](https://github.com/tov-vl/gold-src-ops/actions/runs/38422767261) | `availability-shadow-audit.yml:31:7` |

All paths are under `.github/workflows/`. These were push-time validation
failures, not failed exporter, recovery or audit jobs. The workflows declare
manual triggers, plus a schedule for Availability Evidence; none declares push.
The application PR and main CI passed separately.

## Diagnosis and decision

GitHub explicitly documents
[`environment.deployment: false`](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/control-deployments#using-environments-without-deployments).
It preserves environment secrets/variables and avoids creating a deployment
object. Removing it would change behavior and add deployment history for
operational jobs that do not deploy an application.

All four workflow files are byte-identical between the failed push revision and
current main `cf57f4433d4d3b3034c1f272f2c530e5db6ca0b2`. On that main revision,
Availability Evidence succeeded after the failure:

- [38455831899](https://github.com/tov-vl/gold-src-ops/actions/runs/38455831899), scheduled 2026-10-13 19:33:52 UTC;
- [38484290881](https://github.com/tov-vl/gold-src-ops/actions/runs/38484290881), scheduled 2026-10-13 23:26:49 UTC;
- [38499682443](https://github.com/tov-vl/gold-src-ops/actions/runs/38499682443), scheduled 2026-10-14 02:10:54 UTC.

The evidence is consistent with a transient or event-specific GitHub validation
inconsistency. Its internal cause is not established. No repository syntax defect
has been demonstrated, so retain the workflows, environment boundary, pinned
exporter, schedule, concurrency and permissions unchanged. Do not rerun an
archive writer merely to clear a historical validation failure.

## Verification and limits

Local isolated checks passed on 2026-10-14:

- `tools/smoke/availability-dns-proof.ps1`;
- `tools/smoke/availability-evidence-schedule.ps1` (writer and recovery);
- `tools/smoke/availability-shadow-audit.ps1`.

These checks use fake exporters and verify contracts, failure handling and
cleanup; they cannot validate GitHub's server-side schema. The full local .NET
suite was not repeated for this documentation-only investigation. Recent
successful scheduled evidence is not a fresh live recovery or 24-hour audit.
No application release, production rollout, environment change or manual
operational workflow dispatch is part of this D0 record.

A new documentation push and required PR/main CI provide a bounded check for
recurrence, not proof that every GitHub event parser has recovered. If the same
annotation recurs, preserve the run URL, event, SHA, exact annotation and zero-job
count before considering a workaround. Removing environment isolation or
changing deployment behavior is not an equivalent syntax repair.

The first follow-up branch push at
`71592698776ee9d3f60b384653a7c44aae1f7a65` produced no auxiliary failure runs
in the subsequent API inspection. This is expected for these trigger definitions
and is a non-recurrence observation, not a successful execution of those jobs.
Required documentation PR/main CI is checked as part of delivery.
