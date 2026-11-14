# Archive External Trigger Implementation Plan

Prepared on 2026-11-14 from the [reliability decision](archive-trigger-decision.md).
Status: R3 integration plan; the [offline state core](archive-trigger-progress.md)
is locally implemented. Runtime integration and rollout remain pending. Freeze
a named release and exact revision before execution.

## Outcome And Runtime Boundary

Replace automatic GitHub cron admission with one external UTC-hourly trigger
while retaining the existing GitHub-hosted Archive writer and independent
Recovery workflow. Preserve `:53`, the 24-hour export bound, five-minute source
delay, pinned exporter and storage namespace. Keep ordinary application startup,
database, game server, addon, agent and source probe behavior unchanged.

Add one dedicated non-login service identity, a bounded dispatcher/timer, a
private durable state directory and one dispatch credential on the existing
control plane. Extend the independent Archive observer and existing Operator
projection only as required to identify the automatic owner correctly. Do not
add a second monitoring surface. No new public port or host is needed.

## Single Writer And Admission

1. Keep the existing writer workflow and concurrency group
   `availability-evidence-writer-v1`, with `cancel-in-progress: false`. All
   admitted writes use this path; the dispatcher never uploads to B2.
2. Introduce explicit modes: `legacy`, `external`, `maintenance`, `paused`.
   Default to the accepted legacy behavior during source deployment. In
   external mode the existing schedule-enabled flag is false and cron jobs
   cannot write. In maintenance mode the timer is stopped and drained before
   an explicitly approved manual write. Paused admits no writes. Manual
   Recovery remains a separate read-only path.
3. Bind external requests to the fixed repository, workflow, `main`, accepted
   workflow/helper content digests, exporter pin, deployment generation and a
   unique claim. Record the actual run head and validate these digests at that
   revision. An unrelated main commit is allowed; changed executable workflow
   content is not. Do not require a frozen main head for the whole trial.
   Validate every field in the workflow before secret-bearing export. A
   caller-provided label alone does not prove automatic origin.
4. Reject reruns for the automatic write path (`run_attempt != 1`). A failed
   or cancelled write job cannot be made safe by rerunning it. Update rotation
   and manual catch-up runbooks for the maintenance admission requirement.
5. Hold an OS lock across each local dispatch/reconciliation pass. Keep at
   most one unresolved remote claim; do not enqueue every missed hour.

GitHub concurrency serializes jobs but is not the durable request ledger. Its
default pending slot can be replaced even with cancellation of the running job
disabled. The dispatcher must not rely on an unbounded pending queue or assumed
ordering. See [GitHub concurrency behavior](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency).

The existing storage client uses an existence check followed by a single PUT,
not an atomic create-if-absent storage transaction. Keep one writer across
automatic and maintenance operations and retain all uncertain-write stops.

## Durable Requests And Recovery

Use a small local transactional ledger with a unique `(generation, UTC slot)`
key, schema version and immutable transition history. Store only validated
metadata: claim ID, expected workflow digests/pin, exact requested interval, run ID when
known, state, timestamps and bounded receipt digest/counts. Never store tokens,
raw source samples or provider response bodies in the ledger.

| State | Rule |
| --- | --- |
| Prepared | Exact request recorded; no network attempt authorized yet |
| Attempting | Commit before sending the single POST; a crash here is conservatively uncertain |
| Accepted | Persist a validated returned run ID; this is not Archive success |
| Verified | First-attempt job, pin, exact requested interval and successful receipt all correlate |
| Failed-known | Conclusive pre-write failure retained; no retry of this claim |
| Unknown | Request acceptance or object write cannot be established; block new automatic writes |

Use the documented `2026-03-10` REST version, whose dispatch response includes
`workflow_run_id` and URLs with HTTP 200. Validate the ID and build follow-up
URLs from the fixed trusted host; do not follow arbitrary returned URLs. This
contract must pass a bounded rollout probe. An unexpected 204, malformed body,
timeout, connection loss or crash after send enters Unknown; never resend to
discover whether the first attempt worked. See [GitHub workflow dispatch API](https://docs.github.com/en/rest/actions/workflows#create-a-workflow-dispatch-event).

Reconciliation uses bounded GETs to that run or, if its ID was lost, a bounded
search of this workflow's recent runs using the claim marker. Require exactly
one matching run plus the expected event, ref, accepted content digests, generation
and first attempt. A run title alone is insufficient. No match is not proof
that POST was rejected. Multiple matches, changed pins, contradictory state or
an incomplete search remain Unknown and require operator review.

Add a compact schema-versioned workflow receipt with claim, origin, exact
interval, exporter revision, object digest/length and terminal stage. Publish
only bounded metadata. Mark a potential upload attempt durably in the remote
run before entering the writer. A terminal job without conclusive pre-write
failure or a verified receipt remains uncertain even if GitHub reports failure.
After an unknown PUT, use the existing independent reader to reconcile the
expected object; no repeated PUT, deletion or overwrite is allowed. If the
expected digest was not recovered from the run, preserve the incident for an
explicit investigation instead of re-exporting and guessing it.

Retain terminal claim metadata for 21 days, with an initial cap of 1,000
claims/16 MiB. Implement bounded retirement of older terminal records before
enabling the timer, retaining a daily aggregate checkpoint and retirement audit.
Never retire unresolved claims, records referenced by an active claim or the
seven-day assessment, or rewrite their transitions to unblock the service.
Capacity exhaustion, corruption, disk-full, invalid clocks and unknown schema
all stop admission rather than forcing retirement.

## Clock, Catch-up And Work Limits

- Calendar slots stay UTC `HH:53`. A short periodic reconciliation wake may
  inspect state every five minutes, but only a new eligible slot can write.
  Persistent wake-up coalesces missed slots into the newest eligible slot.
- For slot `T`, request `[T - 5 minutes - 24 hours, T - 5 minutes)` explicitly.
  Reject future windows and stale dispatches older than one slot. Validate the
  same interval again after job admission; a late job must not upload a stale
  window merely to refresh health.
- If a previous claim is pending or unknown, do not dispatch another. Once it
  is conclusively finished, record missed slots and choose at most the newest
  eligible one. A known pre-write failure may allow a later distinct slot;
  the failed claim itself is never retried automatically.
- Retain closed coverage gaps independently of the latest success. Beyond
  24 hours of uncovered time, report the uncovered interval and require a
  separate bounded catch-up decision. Do not expand the lookback, loop over
  history or assume the source still retains every sample.
- Initial dispatcher limits: one POST and at most eight bounded GETs/pass
  (including three reviewed content reads and at most one token-free log redirect),
  10-second HTTP deadlines, 512 KiB metadata bodies, one compact receipt up to
  16 KiB, 90-second service deadline, no automatic HTTP mutation retries.
  Historical reconciliation is capped at two pages of 100 runs; incomplete
  history fails closed. Keep the existing 15-minute Archive job timeout.
- Verify available host capacity before enabling: initial service limits
  128 MiB RAM and 16 tasks. Packaging must not add recurring dependency
  installation. Pin and validate the runtime/package at delivery.

## Credentials And Isolation

Propose one expiring fine-grained GitHub token restricted to this repository,
with Actions write and mandatory metadata read, initially for 90 days. GitHub
does not limit this permission to one workflow or to dispatch alone: it permits
other Actions operations in that repository. Record that real authority in the
approval, not just the narrower paths implemented by the dispatcher. Do not use
a classic `repo` token or the operator's general CLI credential in the service.

Creation, secure transport, service identity and permission changes require the
owner's explicit rollout decision. Prepare the package and exact secret
destination first. Transfer the value through the existing approved hidden-input
and SSH mechanism, never through chat, source, arguments, logs or artifacts.
Use a protected service credential file/systemd credential mount and add its
expiry to the private credential inventory with a rotation owner and deadline.
Do not silently extend the public credential API to arbitrary keys.

The service has no Docker socket, shell dispatch facility, sudo permission,
source/storage secrets, or inbound listener. Only fixed GitHub HTTPS calls are
implemented; TLS validation remains enabled. The separate observer gets a
read-only sanitized claim projection, not access to this token or a writable
ledger. B2/Grafana pairs remain in the current GitHub environment. Their scopes,
retention rules and exporter pin do not change as part of trigger setup.

## Health Contract And Failure Isolation

Keep the current observer as a separate service. Extend it to select evidence
for the active automatic generation: legacy `schedule`, or an externally
claimed `workflow_dispatch`. For the latter, correlate the read-only claim/run
mapping with independently fetched GitHub job/step evidence. Do not trust only
the dispatcher's success field. Require the new receipt-validation step to
succeed; runtime acceptance separately checks the actual receipt and B2 object.

Ordinary manual dispatches, reruns, old generations, skipped jobs and a mere
HTTP 200 from dispatch must not refresh automatic health. Missing or ambiguous
mapping is Unknown. Preserve creation-time age, separate observation time,
the three-hour/18-hour thresholds and the existing IRM route. Explain in the
existing API/Web report that scheduled means the selected automatic path, and
expose its bounded origin so it is not confused with GitHub's literal event.
Do not rename old event records or reinterpret manual history.

The observer must not start reading writer secrets. Its absence and the existing
external watchdog remain visible if the control plane stops. A watchdog does
not repair dispatch or prove source completeness. Confirm that this co-located
trigger is acceptable for the three-hour engineering trial before rollout.

## Implementation Units

| Owner | Planned change |
| --- | --- |
| `ops/availability/` | New dispatcher, transactional state/receipt validation and offline fault fixtures; explicit mode and manual-operation runbook |
| `.github/workflows/availability-evidence.yml` | Admission mode, claim/window validation, first-attempt guard and compact terminal receipt; retain existing writer concurrency |
| `ops/availability/run-export-cycle.ps1` and exporter integration | Machine-readable outcome/digest bridge if required; preserve exporter bytes until a separately reviewed pin change is necessary |
| `ops/availability/archive-monitor.py`, production service/timer definitions | Independent owner-aware observation and new constrained trigger units; bounded state access |
| Existing Archive contracts, Infrastructure source and Web view/report | Compatible origin/semantics update only; preserve Unknown, auth, cancellation and other report sources |
| Existing workflow, exporter, monitor and Archive UI tests | Cross-mode exclusion, crash/timeout recovery, sanitization and compatibility |
| Release/runbook records | Exact package/host files, token authority/expiry, cutover state, rollback baseline and independent receipts |

Do not combine a full exporter migration, new provider, general scheduling
platform, automatic key rotation or unrelated monitoring/gameplay work with this
slice. If frozen exporter bytes prevent the receipt contract, review the narrow
pin advance explicitly rather than rebuilding an already accepted artifact.

## Acceptance And Delivery Sequence

1. Implement offline first. Exercise simultaneous wakes, restart before/after
   POST, response-ID loss, duplicate/delayed runs, job failure before/after PUT,
   ledger corruption/full disk/retirement, clock jumps, token expiry, changed pins,
   unchanged workflow content across unrelated main commits,
   manual/legacy exclusion and bounded catch-up. Use a fake GitHub transport
   and disposable ledger; no production Archive is needed for these cases.
2. Verify source/workflow gates, current exporter tests and focused Archive
   API/Web/report compatibility. Run InspectCode for authored C#, the applicable
   serial Quality Gate and required CI. Freeze an immutable package and exact
   rollout/rollback manifest. Planning alone has no such release evidence.
3. Approve the R3 runtime bundle, service identity and precise token authority.
   Install disabled. Verify read-only identity/API contract and a dispatch to a
   validated no-write probe mode through the same claim flow. No credentials
   for B2 should enter that job's environment. Confirm the actual run-ID response.
4. Enter paused/maintenance mode, disable legacy schedule admission, and verify
   no writer is running or queued. Stop on uncertain in-flight operations.
   Record an activation generation/time; then enable the external owner alone.
5. Accept one controlled first write and independently recover its exact object
   with the reader workflow. Then observe at least two natural hourly slots:
   correct origin/window, one claim/one run, receipt and no overlapping writer.
   Verify existing warning propagation without deliberately stopping production
   export. An isolated synthetic exercise may test failure routing if needed.
6. Promote the exact accepted package and close runtime evidence separately
   from a prospective seven-day reliability assessment. During those seven days
   track age, requested-window gaps, missing claims, uncertain results and
   source completeness separately. Do not wait seven days to continue unrelated
   development or describe early acceptance as a proven freshness guarantee.

Success for the seven-day trial means no automatic-success age above three
hours, no unresolved duplicate/uncertain write and no closed requested-window
gap in that window. Report source missing slots without excluding them or
calling them an export repair. If API/runner delays prevent this result, reopen
the independent-executor option with measured costs and explicit new secrets.

## Rollback And Stop Conditions

Disable the external timer first and persist paused admission. Preserve its
ledger, activation generation and all receipts. Reconcile the single outstanding
claim; wait for or verify the associated writer to finish. Only when no pending
or unknown remote operation remains may the prior legacy mode/observer package
and schedule-enabled flag be restored. Verify exactly one automatic owner and
the original `:53` schedule. Never cancel an uncertain writer to claim rollback
completion, automatically resend it, or delete an archived object.

The code/configuration can return to the old boundary, but created B2 objects
and ledger history remain. A new service identity and token also remain disabled
until separately retired. Preserve the compatible reconciliation reader if the
old observer cannot read new state. This forward-compatible retained-state
boundary must be included in the rollout approval.

Stop on unknown dispatch/upload, unexpected workflow permissions or pin,
competing writers, inability to preserve state, failed independent Recovery,
unproven credential isolation or a rollback requiring deletion. Do not silence
freshness warnings, enable a second writer or extend the 24-hour catch-up bound
to make acceptance appear green.
