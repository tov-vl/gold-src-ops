# GoldSrcOps Roadmap

Updated on 2026-11-18 after Archive trigger rc.4 R3 acceptance, the accepted
automated gameplay/telemetry checks and stable player storage 0.40.0.
The earlier repository planning baseline
is `2b33081022ad54061fa8c8e1f4efef4a3f3ad1e0`; runtime identities remain in their
own release and acceptance records.

This is the current planning order. It replaces the priorities in the
2026-10-28 roadmap proposal; historical release and acceptance records remain
evidence for their own revisions. Updating this plan does not implement or
release the proposed stages. Freeze each delivery boundary before execution
using the [release process](release-process.md).

## Accepted Baseline

GoldSrcOps has two connected goals: a reliable, auditable backend control plane
and a usable CS 1.6 community server with familiar shooting and low downtime.
Development should advance a concrete operator or player outcome.

- Public status freshness, game-event agent observation, owner notifications
  and respawn/protection guidance from the earlier roadmap are complete. See
  [v2.94](v2.94-status-freshness.md), [v2.96](v2.96-agent-monitoring.md),
  [v2.98](v2.98-owner-notifications.md) and [v2.99](v2.99-respawn-rules.md).
- The accepted monitoring baseline includes game-host health, backup, storage,
  resources, credential dates, incident delivery, Archive, polling and retention,
  plus an [independent monitoring watchdog](v2.106-monitoring-watchdog.md).
  [v2.130](v2.130-command-dispatch-liveness.md) adds private command dispatcher
  cycle observation and warnings. [v2.131](v2.131-operator-command-dispatch-health.md)
  exposes them through the Operator API, detail, overview and report.
- The [client lab](client-lab.md) supports bounded autonomous checks. Fresh
  [RU/EN weapon acceptance](client-guns-acceptance.md#fresh-renderer-required-matrix-accepted-on-2026-11-08)
  passed with process-bound OpenGL evidence. This is evidence for those
  scenarios, not arbitrary gameplay, performance or two-player balance.
- The [external Archive trigger rc.4](archive-trigger-progress.md#rc4-candidate-and-r3-acceptance)
  has accepted candidate and R3 evidence, including two natural slots and
  independent Recovery. Its separate seven-day age/window-coverage assessment
  and stable promotion remain pending. Missing source minutes remain a separate
  limitation that a scheduler cannot repair.
- [Multiplayer technical checks](automated-multiplayer-acceptance.md),
  [native producer checks](round-ended-producer-engine-acceptance.md),
  [natural objectives](round-objectives-engine-acceptance.md) and
  [end-to-end telemetry](native-telemetry-acceptance.md) have full PR/main CI and
  independent artifact evidence. Their synthetic actors do not establish human
  gameplay quality, balance or demand.

The [backlog](backlog.md) retains milestone status and acceptance links. This
roadmap owns future priorities; it is not a live production health report.

## Now, Next And Later

| Order | Outcome | Dependency | Completion criterion |
| --- | --- | --- | --- |
| Completed | v2.131 command dispatcher health across Operator views | Accepted v2.130 observation contract | Independent API/Web and authenticated eight-source UI/report acceptance passed |
| Candidate/R3 accepted; assessment pending | Assess the external Archive trigger over seven days | Accepted rc.4, single automatic owner and independent observer | Assess the three-hour age target, receipt-window coverage and source completeness separately; stable promotion remains a separate gate |
| Technical baseline accepted | Native gameplay and real agent/API/PostgreSQL/Reader telemetry acceptance | Pinned isolated engine fixtures | Full PR/main CI and independent downloaded-artifact verification passed; human acceptance remains separate |
| Technical acceptance completed | Queue capacity, concurrent import/delivery and restart acceptance | Existing telemetry harness and verified native inputs | Full PR/main CI, independent artifacts and separate replays passed: deferred bytes and identity preserved, drain without duplicates, permanent failure retained without automatic replay |
| Technical acceptance completed | Parallel telemetry server isolation | Existing harness, two actual agents and synthetic machine credentials | Healthy server progresses during another server's outage; cross-server ingest is denied without a write; retained backlog drains without duplicates and Reader history stays separated |
| Stable and production accepted | [Player-data process-crash recovery](player-storage-0.40.0-release.md) | Paired addon and i386 nVault, schema-2 guard transition | Full CI, production A2S/RCON, both independent Recover checks, same-byte stable ZIP verification |
| Protocol prepared; session pending | Validate the existing mode with two human players | Two independent participants/accounts and a controlled session | Use the prepared protocol/result form; a bounded pilot records observed results, failures and at most one evidenced follow-up |
| After the pilot | Improve one evidenced gameplay problem | Pilot findings | One map/spawn adjustment or mechanic passes technical and player acceptance |
| Later | Human activity analysis and community growth | Real visits and an agreed observation window | Decisions use actual human activity with explicit coverage limits |

Archive investigation and player availability do not block unrelated bounded
development. A useful two-player pilot can proceed when participants become
available. The [pilot protocol](gameplay-pilot.md) and
[result form](templates/gameplay-pilot-result.md) are prepared while waiting;
solo or bot checks do not complete the human gate.

## Accepted v2.131 Operator Command Dispatcher Health

The accepted v2.130 cycle metadata and three existing warning states are now
available through an Operator-only API projection, detail page, eighth overview
card and eighth source in the bounded UTF-8 health report. The complete read
flow passed independent runtime and authenticated browser/download acceptance.
See the [release evidence](v2.131-operator-command-dispatch-health.md).

Preserve these acceptance boundaries:

- Running means recent completed cycle progress, including an empty queue.
  An individual failed RCON response does not by itself mean the loop failed.
- Preserve Running, Error, Disabled, Stale and Unknown distinctions. Missing,
  invalid, duplicate or stale observations must not imply healthy progress.
- Keep worker observation time, API evaluation time and warning observation
  time distinct. Report generation does not refresh their underlying evidence.
- Preserve bounded reads, caller cancellation and source failure isolation.
  Failure of this source must not hide the other seven report sources.
- Apply existing Operator authorization and server-side BFF handling. Do not
  expose command contents, private endpoints, credentials or raw exceptions.
- Verify the projection and rendering with focused tests, then the applicable
  CI, independent runtime acceptance and authenticated UI/download checks.

The R1 release changed API and Web only. Existing monitors, warning policy,
command execution, queue behavior, database and game addon were preserved.
Exact digests and acceptance belong in the release record.

Add monitoring surfaces only for an observed failure
or an unanswered operator question. Another panel or report section is not a
milestone by itself.

## Accepted Archive Trigger And Pending Observation

The [earlier seven-day assessment](archive-trigger-decision.md) found 25 scheduled
Archives against 168 nominal hourly ticks. All 25 jobs/receipts passed, but the
median creation interval was 6h 44m and the success age in that window exceeded three
hours for 54.50% of the reconstructed window. Job queue time was only 4-6 seconds.
The 24-hour lookback prevented closed requested-window gaps in the later part of
the sample; it did not provide timely hourly archiving.

The accepted rc.4 uses an external timer on the existing control plane to
dispatch the existing GitHub writer. GitHub runners and Grafana/B2 secrets stay
in their existing environment. This removes the cron event dependency, not
GitHub API/runner or control-plane host dependencies. Defer an independent
executor unless the bounded trial fails or a stronger independence requirement
is selected.

The [implementation plan](archive-trigger-implementation-plan.md) covers one
automatic owner, durable single-attempt claims, bounded 24-hour catch-up,
uncertain-write reconciliation, precise new token authority and retained-state
rollback. Its engineering target is automatic-success age at most three hours
and no closed requested-window gaps during a prospective seven-day trial, not
a hard SLA. Keep successful execution, receipt, independent Recovery and source
completeness as separate evidence.

The [R3 record](archive-trigger-progress.md#rc4-candidate-and-r3-acceptance)
already records external-owner cutover, retained-history acceptance and
independent Recovery. Keep the separate seven-day assessment open: two natural
slots do not establish the three-hour objective over that window. Preserve
hourly `53 * * * *`, the 1,440-minute window, independent observer and existing
warnings from [v2.117](v2.117-archive-reliability.md).
[v2.119](v2.119-archive-completeness.md) continues to define the distinction
between receipt-window coverage and source missing minutes.

The [2026-11-17 interim read-only checkpoint](archive-trigger-progress.md#interim-read-only-checkpoint-on-2026-11-17)
matched 41/41 natural slots, jobs and receipts through 08:52 UTC, with no closed
requested-window gap or three-hour-age breach in that partial window. It does
not close the seven-day assessment or establish source completeness or fresh
independent object Recovery. Scheduling, thresholds and runtime remain unchanged.

The 24-hour export window is unrelated to the deferred 24-hour human activity
aggregation. Neither scheduling changes nor successful Recovery can reconstruct
measurements that were absent from the source.

## Isolated Queue Acceptance While Waiting

The original accepted telemetry cases deliver pre-imported records with import
disabled during dispatch. The accepted [queue recovery slice](telemetry-queue-recovery-acceptance.md)
now covers the concurrent-loop boundary with small disposable queue limits,
the real agent, HTTP API, PostgreSQL and Reader projection.

Require deferred native bytes to survive capacity pressure, existing identities
and sequence to survive restart, and one persisted row per delivered input.
Permanent failures must retain their bytes/classification across restart;
delivery of other entries is checked with capacity available. Dead letters
continue to occupy capacity. Do not clear queues or add automatic dead-letter
replay to make acceptance pass. This is bounded recovery evidence, not a
throughput benchmark, production rollout or long-duration reliability claim.

Reuse the existing native artifacts locally and require fresh artifacts in full
CI. No game-profile, producer, access, monitoring-policy or Archive change is
part of this slice. Further implementation should follow a concrete uncovered
boundary or operator/player need.

## Parallel Server Acceptance While Waiting

The accepted queue cases exercise one agent at a time. The accepted
[server isolation slice](telemetry-server-isolation-acceptance.md) uses two
concurrent real agents with separate queues and credentials against the same
HTTP API/PostgreSQL. Preserve native bytes and independent source/sequence
identity, prove healthy progress before releasing the other server's outage,
deny a cross-server machine write and recover retained events without duplicates.
Reader checks use different intermediate native histories to detect mixing.
Full PR/main CI and downloaded-artifact checks passed for all four cases and
the existing delivery/queue regressions. Separate local replays of the four
isolation cases also passed.

Reuse existing native artifacts and unit-level authorization/race coverage.
Keep original delivery and queue-recovery regressions. Local checks, full
PR/main CI and independent downloaded artifacts are separate gates. This is
technical process/authorization isolation, not a throughput benchmark or a
new production permission model. Production, Archive and the client lab are
outside this slice; the human gameplay pilot remains separate.

## Player Data Crash Recovery While Waiting

The existing closed-vault backup/reload fixture quits normally. The new bounded
[process-crash check](player-data-crash-acceptance.md) found a real limitation:
after acknowledged scoring/settings changes, SIGKILL retained the old checkpoint
but both journals were empty. Reopening lost the new counters/settings and failed
seven assertions. That original failure remains historical evidence.

The separately approved [fix](player-data-durability-implementation.md)
uses checked journal commit and a verified atomic checkpoint in a reproducibly
built Linux i386 module. Addon 0.40.0 acknowledges only committed saves. Strict
recovery with reversed slots, a second reopen, missing-journal rejection,
save-path failures and the existing regression passed locally and in full hosted
CI. Independently downloaded artifacts and separate replay passed after PR #566.
The [paired installer](paired-player-storage-installer.md) passed the
schema-2 R3 rollout and independent host acceptance. The
[stable 0.40.0 record](player-storage-0.40.0-release.md) covers the rc.1
rollback, both off-host Recover checks and the independently verified stable
ZIP. Power-loss protection, the human pilot and Archive observation remain
separate.

## Gameplay Pilot And Follow-up

The [automated multiplayer technical checks](automated-multiplayer-acceptance.md)
now have local and hosted native-engine evidence for protection, respawn and
client-state isolation alongside the existing combat/persistence suite. These synthetic
checks can run without players; they do not close the human pilot below or
establish map flow, fairness or enjoyment. Full PR/main CI and independent
artifact verification passed; human acceptance remains separate.

The [natural objective checks](round-objectives-engine-acceptance.md) additionally
have local and hosted evidence for real bomb explosion/defuse and round-time
expiry under the managed fast-reentry profile, followed by restart and event
continuity.
These controlled native stimuli preserve normal round timers and do not accept
client planting inputs or player map flow. Full PR/main CI and independent
artifact verification passed; human gameplay acceptance remains separate.

Plan a 30-45 minute controlled session with two independent human players and
accounts. The second client-lab laptop using the same Steam account is not a
substitute. Keep existing ranks, familiar weapon handling and the fast-reentry
mode as the baseline.

Use the [prepared protocol](gameplay-pilot.md) and copy the
[result template](templates/gameplay-pilot-result.md) into a new private session
record. The [Russian participant brief](gameplay-pilot-participants-ru.md) gives
actors A/B their sequence and reporting format. Freeze actual installed
versions/configuration immediately before play;
the preparation's source revision is not an installed-version claim. Distinguish
core and conditional scenarios, retain failed repeats and every untested reason,
and select at most one follow-up. If no evidenced issue is found, select none.

Record actual checks of respawn timing and protection, damage and protection
cancellation on attack, eligible HP/ammunition rewards, team behavior, native
map voting, round objectives and progress across reconnect. Note time to the
next encounter, repeated kills near spawn and unclear rules. Record the tested
build/configuration, failed or untested cases and a prioritized follow-up.

No new addon release is required just to run the pilot. Two players provide
interaction evidence, not retention statistics or a balance claim for a busy
server. Reuse the client lab for visual regressions and change it only when a
specific acceptance scenario needs support.

Choose at most one follow-up from the findings. A small-map or spawn adjustment
must respect the [map menu's exact five-map contract](../samples/amxmodx-map-menu/README.md).
Any mapcycle change needs matching policy and tests. An external plugin needs
reviewed source/license, pinned compatibility, hook and asset checks, plus a
bounded rollback. Avoid duplicating the existing rank and persistence system.

## Deferred Until There Is Evidence Or A Dependency Is Met

- Bots: remain disabled for this plan. Before a bot experiment, address the
  producer's empty/bot-only/mixed roster behavior; its current round guard
  excludes HLTV but does not suppress bot-only activity. This is a prerequisite,
  not a claim of a current production incident.
  The [native producer checks](round-ended-producer-engine-acceptance.md) now
  lock that existing behavior with full PR/main CI and independent artifact
  evidence; a suppression-policy change remains a separate decision.
- Human activity aggregation: wait for real visits and explicit agreement on
  the observation window. Synthetic or solo checks cannot establish demand.
- Plugin packs, combat-advantage VIP, economies, skins and additional modes:
  require a demonstrated player need. Do not accumulate mechanics in advance.
- Generic engine support, service extraction, a broker or infrastructure
  expansion: require observed scaling, ownership or failure-isolation pressure.
- Sandbox entitlements and payments: not near-term priorities; real payments
  remain outside scope pending a separate product decision and sandbox work.

Update this roadmap after each accepted milestone or material dependency
change. Update backlog priorities in the same change, and keep detailed release
evidence in its owning milestone documents.
