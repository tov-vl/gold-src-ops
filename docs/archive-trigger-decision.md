# Archive Trigger Reliability Decision

Decision date: 2026-11-14. Status: design selected for implementation; no runtime
change or release is made by this record. Baseline: accepted v2.131, repository
revision `1930c0458d51e3d5db0e05268d25316fe69a16c7`.

## Decision

Use a bounded external timer on the existing control plane to request the
existing GitHub Archive workflow. Keep export execution on GitHub-hosted
runners, Grafana source credentials and both B2 pairs in their existing GitHub
environment, and B2 as the independent archive. Replace the automatic trigger,
not the writer. Select one automatic owner during cutover; do not add a second
writer or a cron fallback that races the timer.

This is the smallest change supported by the observed failure: long gaps before
workflow creation, followed by prompt job admission and successful execution.
It removes the GitHub `schedule` event dependency, but retains GitHub API,
runner, source and storage dependencies. The timer also shares the control-plane
host failure domain. It is not an independent executor or a guaranteed SLA.
The external probe and stored evidence remain outside both product hosts.

The [implementation plan](archive-trigger-implementation-plan.md) specifies
durable claims, permissions, uncertain results, acceptance and rollback. It
requires a separate R3 implementation/rollout decision. Current production
continues hourly `53 * * * *`, a 1,440-minute lookback and the existing warnings.

## Observed Reliability

Read-only collection inspected the public repository's current workflow, main
revision, scheduler flag, exporter pin, run/job metadata and bounded job logs.
The scheduler was enabled and the workflow active. The verified exporter pin
was `752b1bccd421d141e761349de9b3166c1c1f541a`.

The fixed seven-day measurement window is
`[2026-11-07T12:00:00Z, 2026-11-14T12:00:00Z)`. Collection began at
`2026-11-14T12:13:36Z`; later results are outside this assessment. A preceding
day was included only to establish the left boundary. Jobs and logs for 31
Archive runs and eight existing Recovery runs were inspected; no workflow was
dispatched, rerun or cancelled.

| Measurement | Result | Interpretation |
| --- | --- | --- |
| Scheduled runs created inside the seven days | 25 | The configured cron has 168 nominal hourly ticks in this window |
| Verified first-attempt Archive jobs and object receipts | 25/25 | This confirms these executions, not hourly cadence |
| Interval between scheduled run creation times | Min 3h 27m 14s; median 6h 44m 14s; max 10h 33m 23s | Includes the preceding success as the left anchor |
| Creation to job start | 4-6s; median 5s | The observed hours-long delay is not the queue time of these jobs |
| Job duration | 22-60s; median 32s | Export execution was short in this sample |
| Existing observer age above three hours | 54.50% of the window | An offline continuous-time reconstruction, not sampled Prometheus history |
| Latest accepted scheduled run at the right edge | Created 2026-11-14 07:04:42 UTC; age 4h 55m 18s | Its requested output ends at 07:00 UTC |

For the age reconstruction, a successful run becomes usable at job completion;
its age is measured from creation, as in the existing observer. The prior
accepted run remains the anchor while a new job runs. Manual runs are excluded.
This does not claim that an alert fired for exactly the same duration: the live
observer polls every ten minutes and the warning has a hold period.

The 25/168 comparison is not a measured delivery probability or proof that 143
specific events were dropped. Run metadata does not identify the original cron
slot for each delayed event. GitHub documents that scheduled events can be
delayed or dropped under load. Provider-internal telemetry would be required to
separate those causes here. See [GitHub schedule behavior](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule).

## Window Coverage, Recovery And Source Data

Requested output intervals were taken from successful job receipts, never
inferred from run creation time. Union them and clip to
`[2026-11-07T12:00:00Z, 2026-11-14T11:55:00Z)`: 10,075 mature UTC minutes after
allowing the existing five-minute ingestion delay. Only receipts completed by
the measurement cutoff are included. The raw query overlap does not extend
stored coverage.

| Receipt population | Covered requested minutes | Closed gaps | Open tail |
| --- | --- | --- | --- |
| Scheduled Archive only | 8,947 / 10,075 | Six intervals, 833 minutes total | 295 minutes |
| Scheduled plus the two inspected manual Archives | 9,042 / 10,075 | Five intervals, 738 minutes total | Same 295 minutes |

The closed schedule-only gaps are:

| Start UTC | End UTC | Minutes |
| --- | --- | --- |
| 2026-11-07 23:55 | 2026-11-08 00:39 | 44 |
| 2026-11-08 06:39 | 2026-11-08 07:49 | 70 |
| 2026-11-09 02:56 | 2026-11-09 06:06 | 190 |
| 2026-11-09 12:06 | 2026-11-09 16:39 | 273 |
| 2026-11-10 04:16 | 2026-11-10 06:57 | 161 |
| 2026-11-10 12:57 | 2026-11-10 14:32 | 95 |

These are gaps in the supplied receipt population, not a proof of permanent
data loss or a complete inventory of every B2 object. The last 95-minute gap is
covered by the inspected manual 24-hour Archive. The open tail is a snapshot of
lag at the cutoff, not yet a closed gap.

The window crosses a configuration change. The first scheduled 24-hour receipt
in the sample is [run 43060342226](https://github.com/tov-vl/gold-src-ops/actions/runs/43060342226)
on November 11; ten scheduled runs use that lookback. Their requested intervals
overlap continuously through November 14 07:00 UTC. All six closed gaps above
precede that transition. Therefore the sample supports the value of the
24-hour lookback, while it does not support a claim of timely hourly archiving.

All eight existing Recovery jobs passed first attempt. Their recorded digest
and byte count match six distinct inspected Archive objects, four of them from
scheduled runs. This is historical recovery evidence for those exact objects,
not fresh independent download verification of all 25 scheduled objects.
Representative public runs:

- [Scheduled Archive 43154300742](https://github.com/tov-vl/gold-src-ops/actions/runs/43154300742)
  and [Recovery 43217718327](https://github.com/tov-vl/gold-src-ops/actions/runs/43217718327).
- [Latest inspected Archive 43569365078](https://github.com/tov-vl/gold-src-ops/actions/runs/43569365078).

Receipt coverage and successful Recovery cannot establish source completeness.
The earlier [v2.119 audit](v2.119-archive-completeness.md) found 1,432 good and
eight missing expected minutes in its particular 24-hour source window. No
fresh seven-day source audit was run for this decision. Neither a changed
trigger nor overlapping archives can manufacture missing measurements.

## Selected Objective

Use the following engineering objective for the new automatic path:

- Keep one nominal UTC slot per hour at `:53`. Normal completion is before the
  following slot. Measure latest accepted automatic run age from creation and
  require it to remain at most three hours during the prospective acceptance
  window. Acceptance requires the correct recent slot/window and a completed
  first-attempt writer receipt, not merely a dispatch response.
- Retain the five-minute ingestion delay, 24-hour export bound, existing
  three-hour warning and 18-hour catch-up risk warning. Record the exact output
  end separately from run creation and completion. Run age is not data age.
- Require no closed requested-window gaps within the new observation window;
  disclose its open tail and source-missing slots separately. Never hide an
  unresolved old interval by advancing only the newest watermark.
- One seven-day observation with no age breach validates the engineering trial;
  it does not establish a hard maximum delay, an availability SLO or a permanent
  guarantee. External outages count as misses, not denominator exclusions.

The current cron sample fails the three-hour objective. A 24-hour overlap limits
catch-up exposure but does not satisfy freshness. An interruption beyond the
bounded recovery window needs explicit operator reconciliation.

## Options

| Option | Benefit | Cost and limitation | Decision |
| --- | --- | --- | --- |
| Keep GitHub cron | No new service or credential; existing 24-hour overlap | Measured age exceeds three hours for over half the week; more observers or changing minutes would not establish a guarantee | Keep only until reviewed cutover, not the selected improvement |
| External timer, existing GitHub writer | Replaces the failing event source; retains current export, secrets and Recovery | One small durable dispatcher and a repository-scoped Actions credential; API/runners and timer host remain dependencies | Selected |
| Independent exporter execution | Removes GitHub scheduling and runner dependencies | New execution/packaging lifecycle and distribution of Grafana/B2 credentials; independent hosting adds operating cost; co-location weakens failure separation | Defer unless the selected trial fails due to API/runners or stronger independence becomes required |

Do not invent a hosting quote: no new VM or paid provider is selected. The
existing host has a finite resource budget that must be verified at rollout;
Actions usage should return toward 168 jobs/week instead of the observed 25.

## Evidence And Verification Boundary

The owner-controlled evidence bundle retains bounded run/job projections,
receipt metadata, log digests, analysis and an independent minute-grid coverage
check. Raw logs and credentials are not retained in this decision. A second
calculation reproduced 8,947 covered minutes and the maximum inter-run gap.

The inspected code confirms one B2 PUT with SDK retries disabled and a prior
object-existence check. That check is not an atomic provider lock. Existing
content-addressed keys therefore do not justify racing writers or retrying an
unknown upload. The implementation plan preserves serialization and requires
reconciliation before any further automatic write after uncertainty.

This investigation changed documentation only. It did not change flags, cron,
credentials, running services, source data or archive objects, and did not send
notifications or open a provider support request.
