# Player-search warnings readiness

Status: installed and independently accepted on 2026-10-14. Product PR and
post-merge CI passed; the configuration-only monitoring stage is complete.

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
  dependency, contract or browser behavior changed. Full required PR/main CI
  passed for the monitoring/test changes on the product and merged revisions.

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

The owner explicitly authorized product push/PR/CI/squash merge, this two-file
installation with SIGHUP, independent acceptance, the scoped rollback if needed,
and the combined evidence PR/CI/squash merge. No additional runtime scope was used.

## Delivery and installed identity

[Product PR #350](https://github.com/tov-vl/gold-src-ops/pull/350) was squash merged
from reviewed `353ff174c98e0f40d585aba189d50b2e89a8130f` to deployed
`c5a54a2679740312323d258e5866bcf3558caac0`; their trees are identical.
All five jobs passed in both
[PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38582130600) and
[post-merge CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38583645993).
No application tag/image was published; application stable remains v2.76.0.

The installed hashes match the two reviewed hashes in the table above. The
private operator record retains the fresh baseline, predecessor bytes/metadata,
executor identity, frozen inputs, execution phases and independent readbacks.

## Production acceptance

- Fresh read-only preflight verified all 11 monitoring files, three loaded rule
  groups, healthy dependencies and seven running service identities. The first
  check rejected mode 0664 on three unrelated Grafana files. Comparison with the
  accepted record confirmed unchanged metadata; the check was corrected to
  require that exact predecessor metadata. No permissions were changed.
- Exactly the two reviewed files were replaced with preserved ownership/mode.
  Prometheus saw the exact rule bytes; rule and full-config validation passed.
  One SIGHUP reloaded the existing container. Grafana provisioned the reviewed
  dashboard through its existing directory mount.
- The immediate rule-health gate stopped after reload. A separate readback then
  verified both file hashes, healthy rules without evaluation errors, exact
  normalized expressions/annotations, reviewed dashboard and successful reload.
  The completed installation was reconciled in its evidence journal; there was
  no repeated installation, extra reload, rollback or change to the rule bytes.
- A further independent acceptance passed: all three Web rule definitions and
  Grafana panel queries matched, the other rule groups and nine monitoring files
  were preserved, and all seven containers retained identity, image, start time,
  restart counter and complete mounts. Main config inode, Compose and environment
  hashes stayed unchanged. The atomic file helper separately passed local
  metadata, rollback, scope, symlink-refusal and temporary-file cleanup checks.
- No Web warning was active. Existing `player_search` success telemetry was
  visible, but its 15-minute increase was zero. This proves configured coverage
  and query visibility, not a new successful search or live failure/recovery.
  No search traffic, RCON query, game action or synthetic failure was generated.

## Limits

No traffic or no warning does not prove availability; cleared warnings can
reflect an aged window or lost telemetry. Firing and recovery are demonstrated
by synthetic cases, not production failure injection. Busy remains visible
without its own warning; personal notifications are still deferred. Game-client
visual acceptance, two-player testing and weekly backup observations remain
independent. No application deployment or further production gate is required
for this accepted configuration change.
