# B2 availability key rotation - November 2026

## Status and scope

The operational key rotation passed its read, write, schedule and retirement
checks on 10-11 November 2026. The separate recorded Operator expiry update
also passed independent runtime, monitoring and authenticated browser acceptance
on 11 November. The rotation and its metadata follow-up are accepted.

Two expiring standard application keys replaced the previous archive reader and
writer. Each replacement retained the corresponding bucket, prefix and exact
capabilities, including the existing writer preset's broader permissions.
No scope expansion or narrowing was bundled with this rotation. The owner
retained old and new complete pairs separately in the protected vault until
retirement, and subsequently marked the old records Retired, preserving history.

Only the existing four GitHub environment credential slots were replaced through
the reviewed hidden-input path. Provider key values, identifiers, private scope
values, raw source records and operator recovery material are absent here.
Master/restic credentials, storage configuration, exporter pin, game, agent,
database and existing runtime images were preserved by the key operations.

## Operational acceptance

| Gate | Evidence and result |
| --- | --- |
| Old-reader baseline | [Recovery](https://github.com/tov-vl/gold-src-ops/actions/runs/42789066359) passed before switching credentials. |
| Replacement reader | [Independent Recovery](https://github.com/tov-vl/gold-src-ops/actions/runs/42792344913) passed for the accepted pre-existing object. |
| Stable input | [Read-only preflight](https://github.com/tov-vl/gold-src-ops/actions/runs/42798085934) confirmed two equal bounded exports and deterministic reports without B2 credentials. |
| Replacement writer | [One fingerprint-bound Archive](https://github.com/tov-vl/gold-src-ops/actions/runs/42798308474) succeeded; [independent readback](https://github.com/tov-vl/gold-src-ops/actions/runs/42798505194) matched the object and report. |
| Actual scheduler resumption | [Scheduled Archive](https://github.com/tov-vl/gold-src-ops/actions/runs/42820896948) and [independent Recovery](https://github.com/tov-vl/gold-src-ops/actions/runs/42827000915) passed after restoring the original scheduler value. |
| Exact old-key retirement | The owner explicitly confirmed both old provider identities. Each was revoked once, with independent refreshed-list verification of absence and preservation of both replacements, master and restic. |
| Read after retirement | [Separate Recovery](https://github.com/tov-vl/gold-src-ops/actions/runs/42834926499) passed after both revocations, using the new reader only. |
| Ordinary write after retirement | [Actual scheduled Archive](https://github.com/tov-vl/gold-src-ops/actions/runs/42897078452) succeeded after both revocations; the successful upload receipt and fixed-source check were independently accepted. |

Both scheduled-object and post-retirement readback confirmed 366 records,
136096 bytes, content hash and deterministic-report equality. The final ordinary
post-retirement write stored 137230 bytes. No duplicate upload, automatic
uncertain retry, manual replacement for a scheduled gate or retained raw log was
used as acceptance. Read-only Recovery jobs did not receive writer credentials.

## Accepted recorded metadata follow-up

The existing [minute-precision register](v2.115-minute-credential-expiry.md)
supports the provider's actual expiry precision. The applied update changed
only the two B2 minute deadlines and their two historical provider-verification
times. Exact-second B2 expiry fields remain empty; Grafana records were retained.
The verification time comes from the real provider list check after retirement,
not a later workflow run or page refresh.

Only API was recreated with its already accepted v2.116 digest. Independent
checks confirmed exactly four changed date values, the accepted image and
provenance, preserved file ownership/modes, and unchanged identities, mounts and
network addresses of all 14 other captured containers, including Web.

Ten focused local installer tests passed exact patching, JSON snapshot stability
and rejection of duplicate, missing, injected or imprecise date fields. The first
independent checker stopped because it incorrectly required Compose's
configuration-hash label to remain unchanged after the configuration update.
Read-only diagnosis confirmed that this was the only changed API label. A
separate checker revision, with six focused label checks, then passed; no second
configuration application or rollback was performed.

All four current credential purposes are Recorded, all 23 credential rules are
healthy, and the previous B2 Soon alerts cleared at the source. Public readiness,
liveness and status checks passed; anonymous protected API/page requests remained
denied or redirected. The authenticated Operator page showed both new B2 minute
deadlines and their historical verification time, retained both Grafana records,
and displayed the precision and recorded-metadata disclaimers after refresh.
No separate IRM/email recovery claim is made by these source-alert checks.

No product source, image build or new release tag was needed. The full .NET
suite and InspectCode were not run for this metadata and documentation follow-up;
no C# code changed. Local documentation checks are separate from runtime evidence.

## Anomalies and claim limits

The initial scheduler-pause deadline expired while waiting for continuation.
The deadline and original receipts were retained unchanged. A separately
approved bounded completion window was recorded before further Archive work;
the original scheduler value was restored within that completion window. Total
pause was approximately 64 minutes. No uninterrupted archival claim is made
for that interval.

Read-only investigation found longstanding multi-hour intervals between actual
GitHub schedule runs despite the hourly cron. Workflow state, repository
activity and Actions settings were checked. The missing run was localized
before workflow execution; internal provider scheduling cause was not proven.
No cron edit, disable/re-enable operation, external trigger or support message
was performed. A later successful run closes the rotation's scheduled gate,
not the separate hourly-cadence reliability concern.

Successful archive/recovery proves the bounded operations above, not continuous
source coverage, every future schedule run, general provider health or a future
credential-validity guarantee. After confirmed old-key revocation, restoring
old saved pairs is no longer a working rollback path. Metadata rollback is
separate and cannot restore provider access.

Related: [archive scheduler runbook](../ops/availability/README.md),
[credential register](v2.114-credential-expiry.md),
[recorded-expiry alerts](v2.116-credential-notifications.md).
