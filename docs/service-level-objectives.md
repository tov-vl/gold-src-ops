# GoldSrcOps Initial Service-Level Objectives

Review date: 2026-09-04. Status: reviewed draft with terminal v2.3 SLI
evidence and an accepted `API-01` measurement design; no objective is active or
achieved.

## Purpose And Scope

This document defines the first reviewable SLI and SLO proposal for the
single-node GoldSrcOps reference deployment and its one controlled GoldSrc
endpoint. It is an operating hypothesis, not a release claim. The completed
24-hour v2.3 soak supplies a bounded baseline only; it cannot establish a
rolling monthly objective retroactively.

An SLI is the measured ratio or duration. An SLO is the target applied to that
SLI over a stated window. Release-soak bounds remain separate fail-closed
acceptance criteria in [v2.3 readiness](v2.3-readiness.md).

The proposal does not claim high availability, provider availability for every
managed server, or real-player adoption. It also does not turn Collector target
health into public API availability.

## Measurement Policy

- Ratio objectives use UTC rolling windows and are evaluated at least daily.
- Missing external-probe or recording-rule samples count as bad. A proven
  measurement defect documented before review may invalidate and restart a
  measurement window, but missing data never becomes success.
- Planned maintenance consumes the public API error budget. Polling objectives
  exclude an endpoint only while it was already persisted as disabled; an
  interval must not be removed retroactively.
- Pre-declared recovery exercises and synthetic commands are reported
  separately and do not enter user-traffic populations. Unplanned failures stay
  in the population.
- Low-volume event objectives remain unevaluated until their minimum population
  is reached. Raw counts and every slow or failed event are still reported.
- A budget breach triggers investigation and a change review. It never permits
  automatic RCON replay, deletion of durable work, or suppression of incidents.

## Draft Objective Register

All targets below are proposed and inactive. The activation gates prevent an
objective from being marked healthy merely because its measurement does not yet
exist.

| ID | Service behavior and SLI | Draft objective | Error budget | Activation gate |
| --- | --- | --- | --- | --- |
| `API-01` | External one-minute `GET /health/ready` probes are good when DNS and certificate validation succeed and the response is HTTP `200`; a missing sample is bad. | At least 99.5% good samples over 30 rolling days. | At most 216 bad minutes in a 30-day window. | The independent probe must satisfy the [v2.4 external availability contract](v2.4-external-availability-monitoring.md), record an activation tuple, and persist a complete prospective window. |
| `MON-01` | Poll coverage is durable snapshots divided by expected poll slots while each endpoint is enabled. A slot is good when its snapshot is persisted within two configured intervals plus 10 seconds. | At least 99.5% per endpoint over 30 rolling days. | At a 60-second interval, at most 216 missing slots per endpoint in 30 days. | A recording rule or durable evaluator must retain interval-level coverage rather than only a terminal aggregate. |
| `MON-02` | Poll success is reachable persisted snapshots divided by all persisted attempts for each enabled endpoint. | At least 99.0% per endpoint over 30 rolling days. | At a 60-second interval, at most 432 failed attempts per endpoint in 30 days. | Endpoint identity and enabled intervals must be available to the query; provider and network failures remain in this composite SLI. |
| `MON-03` | Poll freshness is good at each one-minute evaluation when the latest successful snapshot is no older than two configured intervals plus 10 seconds. | At least 99.5% per endpoint over 30 rolling days. | At most 216 stale evaluation minutes per endpoint in 30 days. | A continuous freshness query must exist; the terminal maximum-gap value alone is insufficient. |
| `INC-01` | Detection latency runs from the first persisted failed snapshot in a threshold-qualified sequence to `AvailabilityIncident.OpenedAtUtc`. | At least 95% within `(failure threshold - 1) * poll interval + 10 seconds` over 90 rolling days, with at least 20 incidents. | At most 5% of eligible incidents outside the bound. | The query must correlate the incident with its exact failed-snapshot sequence. Before 20 incidents, report every duration without an achievement claim. |
| `INC-02` | Recovery-recording latency runs from the first persisted successful snapshot after an open incident to `ClosedAtUtc`. | At least 95% within 10 seconds over 90 rolling days, with at least 20 recovered incidents. | At most 5% of eligible recoveries outside the bound. | The query must correlate each close with the first successful snapshot. This measures recording latency, not the unknown time at which the remote server actually recovered. |
| `CMD-01` | Command terminal completion is the share of accepted commands that reach `Succeeded` or `Failed` within 65 seconds of `RequestedAtUtc`. | At least 99.0% over 30 rolling days, with at least 20 non-synthetic commands. | At most 1% late or stranded accepted commands. | A duration query and an alert for non-terminal commands older than 65 seconds must be deployed. Success and failure outcomes remain separately visible. |
| `ALT-01` | Alert queue age is good at each one-minute evaluation when no pending event is older than 15 minutes; missing samples are bad. | At least 99.0% good samples over 30 rolling days. | At most 432 bad minutes in a 30-day window. | External alert delivery must be enabled and the oldest-pending-age series must be retained continuously. |
| `ALT-02` | Dead-letter rate is newly dead-lettered events divided by events created while delivery is enabled. | At most 1.0% over 30 rolling days, with at least 100 created events. | At most one new dead letter per 100 created events. | Counter resets and deployment boundaries must be handled by the query; before 100 events, report counts only. |
| `BKP-01` | Backup freshness is good at each hourly evaluation when the latest successful encrypted off-host backup and repository check are no older than 36 hours. | At least 99.5% good hourly samples over 30 rolling days. | At most three bad hourly samples in a 30-day window. | The freshness marker must be evaluated hourly from outside the backup job and missing samples must count as bad. |
| `BKP-02` | Restore cadence is good when an isolated restore rehearsal from a scheduled off-host snapshot completes successfully within every rolling 35-day period. | 100% of due rehearsal periods. | No missed due period; this low-frequency control has no fractional budget. | Each rehearsal must verify repository integrity, database restoration, expected migrations, and the documented durable-record checks. |

The numeric targets are intentionally modest for a first single-node service.
They should be reviewed after at least one complete measurement window. A later
multi-node design may justify stricter objectives, but infrastructure shape does
not by itself change an SLO.

Poll latency, Collector scrape health, CPU, memory, disk, and database size stay
as diagnostic or capacity indicators. They do not become user-facing SLOs until
a concrete service expectation and a continuous measurement population exist.

## API-01 Activation State

Decision 19 and the
[v2.4 external availability contract](v2.4-external-availability-monitoring.md)
define the primary probe, minute-slot population, missing-data behavior, result
schema, and activation lifecycle. `API-01` is still in the `Draft` stage.

The following work remains before its official window can begin:

- select a managed provider with raw per-execution export and sufficient
  retention;
- configure one primary `/health/ready` probe and separate diagnostic probes;
- validate failure classification, missing slots, retry behavior, and alert
  routing;
- complete a 24-hour shadow run and record the activation timestamp, owner,
  monitor and evaluator revisions, primary location, report query, and alert
  route.

Shadow samples and the completed v2.3 soak do not enter the official
denominator. The first met-or-missed decision can be made only after 30 complete
forward-looking days from the recorded activation timestamp.

## Terminal v2.3 SLI Record

The values below were transcribed only after the expected completion time of
`2026-09-03T16:23:57Z`. Only sanitized aggregates belong in Git; owner-only
JSON, host addresses, endpoint addresses, identifiers, credentials, command
payloads, and raw logs remain outside the repository.

| Observation | Authoritative field or source | Sanitized value | Interpretation |
| --- | --- | --- | --- |
| Soak window and decision | Control-plane evidence `Window`, `Result`, and `TargetEvidence` | 24.095 hours; `Passed`; target evidence; 18/18 checks passed | The bounded release interval completed without changing the candidate or runtime. |
| Candidate continuity | Control-plane evidence `Candidate` plus container continuity checks | `2.3.0-rc.5`; source, image, migration, and all six required containers matched the baseline; zero restart deltas | This establishes continuity only for the observed candidate and interval. |
| Sampled external HTTPS health | Timestamped operator heartbeat summary and terminal probe | 10/10 completed scheduled evaluations plus the terminal probe observed healthy public liveness and readiness; one scheduled trigger produced no result; maximum completed-sample gap was 12 h 38 min 34 s | These are discrete observations, not continuous API uptime. |
| Host-local HTTPS edge | `Indicators.HttpsEdge` | Liveness `200`; readiness `200` | Caddy and the database-backed API path were healthy at the terminal check. |
| Poll coverage | `Indicators.Polling.Expected` and `Total`, recomputed with the corrected floating-point ratio | 1,444 persisted polls / 1,445 nominal slots = 99.9308% | Bounded scheduler continuity for the one controlled endpoint. |
| Poll success | `Indicators.Polling.Successful`, `Failed`, and `SuccessPercent` | 1,444 successful; 0 failed; 100.0% | Composite A2S result across application, network, and game host. |
| Poll latency | `Indicators.Polling.LatencySampleCount`, `AverageLatencyMs`, `P95LatencyMs`, and `MaximumLatencyMs` | 1,444 samples; average 18.64 ms; p95 21 ms; maximum 23 ms | Terminal distribution over every successful poll, not a long-term latency objective. |
| Poll continuity and bots | `Indicators.Polling.MaximumGapSeconds` and `BotPositivePolls` | Maximum gap 60.58 s; 0 bot-positive polls | The 60-second schedule remained within the 130-second release bound. |
| Incident state | `Indicators.Incidents` | 0 opened; 0 currently open; maximum duration not applicable | A zero-event window does not establish an incident SLO. |
| Command state | `Indicators.Commands` | Total/succeeded/failed/incomplete: 0/0/0/0 | No command population existed from which to infer `CMD-01`. |
| Alert outbox state | `Indicators.AlertOutbox` | Created/processed/pending/dead-lettered: 0/0/0/0 | Delivery remained disabled after the controlled exercise. |
| Telemetry continuity | Historical terminal Prometheus query and `Indicators.Telemetry` | Both targets had 5,783/5,782 expected samples, all healthy; capped coverage 100.0% | Private telemetry continuity is not public API uptime. |
| Terminal capacity | `Indicators.Capacity` | Database 9,149,463 B; poll table 835,584 B; free disk 69.86 GiB; available memory 6,810.73 MiB; 4 processors; load 0.08 | A terminal capacity observation, not sustained utilization evidence. |
| Game-host process continuity | Game-host evidence `Result`, `TargetEvidence`, and `Continuity` | `Passed`; target evidence; 8/8 checks and all seven continuity flags passed | The active boot-disabled service, process identity, restart count, and single owned UDP listener matched the baseline. |
| Trial-period address stability | Owner-only start/end comparison | Passed across the bounded trial; one current global address matched the configured value at terminal comparison | Raw address values remain owner-only. |

One monitoring-path anomaly occurred during the window: a local VPN route lost
HTTPS and SSH reachability while an independent external probe still observed
healthy public HTTPS. The route was corrected and later checks passed. This was
not classified as a production outage, but together with the 12-hour sampling
gap and one incomplete scheduled trigger it keeps `API-01` inactive.

The initial terminal evidence rounded fractional coverage to integer values
because of PowerShell overload resolution. The release branch corrects that
calculation and adds fractional boundary tests. The poll-coverage value above
is recomputed directly from the retained counts; telemetry sample counts were
verified at the terminal evaluation timestamp.

## Completion Procedure

1. After the expected completion timestamp, run the control-plane evaluator
   without `-AllowIncomplete` and require `Passed` plus `TargetEvidence: true`.
2. Run the read-only game-host evaluator and require `Passed`, target evidence,
   and every continuity flag to be true.
3. Complete the owner-only trial-period address comparison and retain its raw
   values outside Git.
4. Transcribe only the sanitized aggregate fields into the terminal SLI table.
5. Review the proposed objectives without changing a target to make the
   historical soak appear successful. Accept a target prospectively, revise it
   with a written rationale, or leave it inactive until its measurement gate is
   implemented.
6. Record an activation timestamp, owner, dashboard query, and alert policy for
   every objective that is accepted. Its first achievement decision can occur
   only after a complete forward-looking window.

The release can use a passing terminal record as v2.3 readiness evidence. It
must still describe every initial SLO as proposed rather than achieved.
For this review, every proposal remains inactive because its forward-looking
activation gate and complete measurement window have not yet been satisfied.
