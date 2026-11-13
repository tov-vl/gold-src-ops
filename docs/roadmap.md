# GoldSrcOps Roadmap

Reviewed on 2026-11-13 against accepted v2.130 and repository revision
`43778cf61cbee530afeb4acb6a8b805c1fa939cd`.

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
  cycle observation and warnings. Its Operator projection is the next gap.
- The [client lab](client-lab.md) supports bounded autonomous checks. Fresh
  [RU/EN weapon acceptance](client-guns-acceptance.md#fresh-renderer-required-matrix-accepted-on-2026-11-08)
  passed with process-bound OpenGL evidence. This is evidence for those
  scenarios, not arbitrary gameplay, performance or two-player balance.
- Archive schedule irregularity and missing source minutes remain separate
  limitations. Existing diagnostics and notifications expose them; they do not
  guarantee hourly execution or complete source data.

The [backlog](backlog.md) retains milestone status and acceptance links. This
roadmap owns future priorities; it is not a live production health report.

## Now, Next And Later

| Order | Outcome | Dependency | Completion criterion |
| --- | --- | --- | --- |
| Now | Keep one current roadmap and distinguish historical release context | Accepted v2.130 evidence | Roadmap, backlog priorities and project brief agree; historical evidence is preserved |
| Next release | v2.131: command dispatcher health in the Operator UI and report | Accepted v2.130 observation contract | One read-only API/UI/report delivery passes independent acceptance |
| Parallel | Decide the required Archive freshness and scheduling guarantee | Fresh schedule/job evidence and existing completeness diagnostics | Record acceptable delay, catch-up limits and a decision to retain or replace the trigger mechanism |
| Next gameplay milestone, when people are available | Validate the existing mode with two human players | Two independent participants/accounts and a controlled session | A bounded pilot records observed results, failures and one prioritized follow-up |
| After the pilot | Improve one evidenced gameplay problem | Pilot findings | One map/spawn adjustment or mechanic passes technical and player acceptance |
| Later | Human activity analysis and community growth | Real visits and an agreed observation window | Decisions use actual human activity with explicit coverage limits |

Archive investigation and player availability do not block unrelated bounded
development. A useful two-player pilot can proceed when participants become
available without waiting for v2.131. If no second player is available, prepare
the pilot checklist; do not treat solo or bot checks as its completion.

## Next Release: v2.131 Operator Command Dispatcher Health

Expose the accepted v2.130 cycle metadata and three existing warning states
through an Operator-only API projection, a detail page, an overview card and
the bounded UTF-8 health report. Deliver the user-facing flow together unless
implementation reveals a concrete compatibility or rollout reason to split it.

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

No new monitor, warning policy, command probe, retry, replay, queue change,
migration or game-addon change is planned. Exact runtime scope, risk class and
rollback inputs belong in the delivery plan, not an assumed roadmap approval.

After this gap is closed, add monitoring surfaces only for an observed failure
or an unanswered operator question. Another panel or report section is not a
milestone by itself.

## Parallel Archive Reliability Decision

Keep the existing hourly `53 * * * *` schedule, 1,440-minute scheduled export
window, independent age observer and warning policy while evaluating the
required guarantee. [v2.117](v2.117-archive-reliability.md) defines the accepted
behavior and [v2.119](v2.119-archive-completeness.md) separates requested-window
coverage from missing source minutes.

The next decision record should:

1. Define the acceptable age of the latest successful scheduled Archive and
   tolerance for gaps. Existing three-hour and 18-hour warnings are operational
   thresholds, not proof that a chosen reliability target is met.
2. Compare fresh scheduled run/job evidence with requested-window coverage.
   Keep successful execution, object receipt, independent Recovery and source
   completeness as separate evidence. Manual runs do not establish cadence.
3. Compare retaining best-effort scheduling with an independent trigger or
   execution path. A trigger that still depends on GitHub runners does not
   remove that execution dependency. Preserve one writer, immutable objects,
   bounded catch-up and reconciliation of uncertain writes in every option.
4. If the required guarantee is unmet, scope a separate implementation and its
   permissions, durable state, rollback and acceptance. Do not add another
   observer or change cron merely to conceal the known limitation.

The 24-hour export window is unrelated to the deferred 24-hour human activity
aggregation. Neither scheduling changes nor successful Recovery can reconstruct
measurements that were absent from the source.

## Gameplay Pilot And Follow-up

Plan a 30-45 minute controlled session with two independent human players and
accounts. The second client-lab laptop using the same Steam account is not a
substitute. Keep existing ranks, familiar weapon handling and the fast-reentry
mode as the baseline.

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
