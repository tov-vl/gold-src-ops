# Player-search warnings readiness

Status: implemented and verified locally on 2026-10-14; remote CI and production
installation remain pending. This record does not claim current live coverage.

## Scope and policy

R3 configuration-only: extend the existing public Web API warning and dashboard
to `player_search`. Base source is merged `74871fbac97c25cbb33336a957bc5cba8c1f8df6`.
The [runbook](web-public-api-alerts.md) owns thresholds, interpretation and recovery.
No new application version, signed tag, image publication, game restart, migration,
backup change or personal notification destination is required.

- Preserve the existing 15-minute window, minimum 10 admitted outcomes, minimum
  5 failures, 20% failure ratio and five-minute hold, separately per operation.
- Error and timeout remain failures. Search-only `not_found` is also a failure:
  `PublicPlayerSearchClient` uses it for missing API routes, which display as
  unavailable. A valid empty match list is `success`. Optional 404 for the
  other four operations remains non-failing.
- Busy and canceled traffic stays visible but neither fills the minimum nor
  dilutes the ratio. The dashboard ratio uses the same failure definition;
  p95 uses recognized admitted outcomes. There is no separate busy warning.
- Preserve alert identity, bounded labels and other rule groups. No nickname,
  identifier, query or private endpoint is added to telemetry or public evidence.

## Reviewed file identity

Only these two existing runtime files change. Predecessor hashes are from the
reviewed repository baseline and agree with the accepted v2.73 record; a fresh
production comparison is still required before any write. Never substitute live
bytes for expected values merely to pass a drift check.

| File under observability | Predecessor SHA-256 | Reviewed SHA-256 |
| --- | --- | --- |
| `rules/web-public-api.yml` | `2ecae1f576094491c8ab105d778078085736384a047581109a6e3e8c0917519f` | `ee6a2a3844ae7b3516600572a88b44964d60cc7995b5612fb0e14cd1ee15ba6a` |
| `grafana/dashboards/goldsrcops-web-public-api.json` | `10e20cd20ea8249ea35e17cb9a2577c4106a8eaea72bab54a4967c84f370516f` | `1913ed84c680187237556bd8dff79a4abb22a332d752f8ab356166864ad1789a` |

`prometheus.yml`, Compose, environment, Collector, datasources, other dashboards,
application images and game configuration are outside the write scope.

## Verification

- Pinned Prometheus: 30 public-read scenarios passed, including search hold/fire,
  successful empty result, missing route, busy/canceled-only traffic, minimum
  volume, dilution resistance, operation isolation and recovery. Two cases
  evaluate the actual dashboard ratio against an independent expected 0.5.
- The same run passed all 25 leaderboard and 21 backup scenarios (76 total).
- Pinned Collector/Prometheus/Grafana stack passed OTLP, empty-data queries,
  dashboard provisioning and recovery checks. A disposable rule-file copy
  exercised rollback to a four-operation policy, rejection of malformed rules
  while retaining the last loaded definitions, and restoration of the reviewed
  five-operation rules. Other groups and the Prometheus container identity stayed
  unchanged. Existing config-removal/invalid-config recovery also passed.
- The complete local .NET/browser suites were not repeated: no application code,
  dependency, contract or browser behavior changed. Full required PR/main CI is
  still necessary for the monitoring/test changes.

## Installation, independent acceptance and rollback plan

1. Freeze the reviewed commit after CI and compare its two file hashes above.
   Capture a fresh read-only target baseline: all monitoring hashes/metadata,
   current loaded rules, dashboard, seven container identities and complete mount
   multisets, Compose/environment hashes. Require the expected predecessors and
   healthy dependencies. Keep saved bytes and private details outside Git.
2. Validate with the pinned running Prometheus version. Preserve both predecessor
   files and metadata for rollback. Replace only the reviewed rule and dashboard
   through existing directory mounts. Leave the individually mounted main config
   and its inode unchanged.
3. Check the rule bytes inside Prometheus and full config with `promtool`, then
   SIGHUP the exact container. Require a successful reload and no restart. Wait
   for Grafana to provision the reviewed panel/query definitions.
4. Use a separate readback to verify both hashes, loaded search selectors and
   search-only 404 branch, healthy rules, unchanged other definitions, dashboard
   queries, runtime identity/mount continuity and unrelated monitoring inventory.
   Report live pending/firing warnings rather than hiding them. Existing fresh
   search observations can prove visibility; no new RCON lookup or synthetic
   production failures are required.
5. If acceptance fails, reconcile any uncertain write first. Restore both saved
   predecessor files/metadata, validate and reload, and independently verify the
   former rule definitions, dashboard and unchanged runtime. Do not delete the
   pre-existing rule/group, clear monitoring history or recreate containers.

Before installation, obtain authorization for the exact remote update and its
rollback boundary under the repository's remote-write rule. The planned change
is reviewable here; this record does not itself grant authorization.

## Remaining gates and limits

- Ordinary push, PR, full required CI, squash merge and post-merge CI.
- Fresh target baseline, authorized two-file installation and independent
  production acceptance; then one combined evidence update.
- No live firing or restoration claim comes from synthetic tests. No traffic or
  no warning does not prove availability; cleared warnings can reflect an aged
  window or lost telemetry. Game-client visual acceptance, two-player testing and
  weekly backup observations remain independent.
