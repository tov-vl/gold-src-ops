# GoldSrcOps Initial Service-Level Objectives

Review date: 2026-09-03. Status: draft; no objective is active or achieved.

## Purpose And Scope

This document defines the first reviewable SLI and SLO proposal for the
single-node GoldSrcOps reference deployment and its one controlled GoldSrc
endpoint. It is an operating hypothesis, not a release claim. The active
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
- Missing external-probe or recording-rule samples count as bad unless a proven
  measurement defect is documented before the review. Missing data never counts
  as success.
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
| `API-01` | External one-minute `GET /health/ready` probes are good when DNS and certificate validation succeed and the response is HTTP `200`; a missing sample is bad. | At least 99.5% good samples over 30 rolling days. | At most 216 bad minutes in a 30-day window. | An independent external probe must persist timestamped results continuously for a complete window. |
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

## Terminal v2.3 SLI Record

`Pending` means the value must not be filled before
`2026-09-03T16:23:57Z`. Only sanitized aggregates belong in Git; owner-only JSON,
host addresses, endpoint addresses, identifiers, credentials, command payloads,
and raw logs remain outside the repository.

| Observation | Authoritative field or source | Sanitized value | Interpretation |
| --- | --- | --- | --- |
| Soak window and decision | Control-plane evidence `Window`, `Result`, and `TargetEvidence` | Pending | Must show at least 24 hours, `Passed`, and target evidence. |
| Candidate continuity | Control-plane evidence `Candidate` plus container continuity checks | Pending | Version, source revision, image digest, migration revision, and required containers must match the baseline. |
| Sampled external HTTPS health | Timestamped operator heartbeat summary | Pending | Record good/total samples and maximum sample gap; do not label the ratio as continuous API uptime. |
| Host-local HTTPS edge | `Indicators.HttpsEdge` | Pending | Record terminal liveness and readiness status through Caddy. |
| Poll coverage | `Indicators.Polling.Expected`, `Total`, and `CoveragePercent` | Pending | Bounded scheduler continuity for the one controlled endpoint. |
| Poll success | `Indicators.Polling.Successful`, `Failed`, and `SuccessPercent` | Pending | Composite A2S result across application, network, and game host. |
| Poll latency | `Indicators.Polling.LatencySampleCount`, `AverageLatencyMs`, `P95LatencyMs`, and `MaximumLatencyMs` | Pending | Terminal 24-hour latency distribution over every successful poll, not a long-term latency objective. |
| Poll continuity and bots | `Indicators.Polling.MaximumGapSeconds` and `BotPositivePolls` | Pending | Must remain within the release bound and contain zero bot-positive polls. |
| Incident state | `Indicators.Incidents` | Pending | Record opened, currently open, and maximum duration counts without inferring an incident SLO from a zero-event window. |
| Command state | `Indicators.Commands` | Pending | Record total, succeeded, failed, and currently incomplete commands. |
| Alert outbox state | `Indicators.AlertOutbox` | Pending | Record created, processed, pending, and dead-letter counts; delivery is disabled outside the controlled exercise. |
| Telemetry continuity | `Indicators.Telemetry` | Pending | Record application-export and Collector sample and healthy-sample coverage; this is not public API uptime. |
| Terminal capacity | `Indicators.Capacity` | Pending | Record sanitized database sizes, free disk, available memory, processor count, and one-minute load. |
| Game-host process continuity | Game-host evidence `Result`, `TargetEvidence`, and `Continuity` | Pending | Must show `Passed`, target evidence, and every continuity flag true. |
| Trial-period address stability | Owner-only start/end comparison | Pending | Publish only pass/fail and observation bounds, never the address. |

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
