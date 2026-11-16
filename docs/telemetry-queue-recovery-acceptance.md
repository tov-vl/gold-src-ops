# Telemetry Queue Capacity And Recovery Acceptance

This isolated slice extends the [accepted telemetry harness](native-telemetry-acceptance.md)
to run the real import and delivery loops together. It preserves the current
producer, spool/HTTP contracts, worker code, retry limits and dead-letter policy.
Accepted through [PR #559](https://github.com/tov-vl/gold-src-ops/pull/559), full
PR/main CI and independent downloaded-artifact acceptance on 2026-11-16.

## Existing Coverage And Missing Boundary

SQLite tests already cover atomic capacity rejection and source/sequence
continuity. Dispatcher tests cover backoff, acknowledgement, permanent rejection
and attempt/age limits. The preceding real-process harness covers single-record
delivery, lost acknowledgement and HTTP 503 with restart, with import disabled
during dispatch. This slice adds their concurrent-loop boundary, rather than
duplicating those unit suites.

## Controlled Cases

| Case | Input and fault | Required evidence |
| --- | --- | --- |
| Bounded capacity | Three verified natural-objective records, queue capacity two, proxy HTTP 503 before forwarding | Two queued events, one byte-identical deferred spool record, unchanged next sequence after the rejected import, completed blocked import cycle and no inbox rows |
| Restart and drain | Stop only the owned agent, retain its queue/spool, restore forwarding and restart both loops | Same source and queued identities/payload hashes, sequences 1-3, no observed depth above two, three inbox rows, matching Reader projection, empty queue/spool and completed idle cycles |
| Permanent failure | Separate fresh server/source, capacity three, proxy HTTP 409 for one native occurrence | One durable conflict with one attempt, two other records delivered and visible to Reader, unchanged dead-letter bytes/identity/classification after a new process completes import/delivery cycles, no automatic replay |

All inputs are the exact accepted native bytes. A separate synthetic server for
the permanent-failure case keeps identical native occurrence times from mixing
with the capacity case's Reader history. The first API outage commits nothing;
the conflict is injected before the real API. Successful deliveries use the
actual API, production PostgreSQL migrations and real JWT validation.

The blocked import outcome comes from the actual worker health record. Restart
checks wait for fresh completed cycles from the new process, rather than an
arbitrary sleep. Queue snapshots are single-statement read-only SQLite reads.
The harness never updates a queue row, lease, sequence, receipt or dead letter.
It samples the queue depth while the existing atomic-capacity unit test owns the
transactional invariant.

Dead letters still occupy capacity. This case verifies progress with available
capacity; it does not promise continued intake into a queue filled by dead
letters. The accepted queue and retained test conflict are removed only during
disposable test teardown after the evidence has been checked.

## Execution And Evidence

Use the same wrapper and matching accepted engine/fixture inputs:

```sh
python3 -B tools/smoke/test-game-event-telemetry-e2e.py
python3 -B tools/smoke/game-event-telemetry-e2e.py --queue-recovery \
  --evidence <accepted-engine-output>/evidence \
  --compiled <matching-compiled-fixtures> \
  --output <new-queue-recovery-output>
```

The new mode is explicit; the original three delivery cases remain the default.
Admission still requires the current fixture/source and engine-lock hashes,
successful native markers and exact objective receipts. There is no synthetic
runtime fallback. Result validation binds all three native hashes to the queue,
restart and retained-conflict evidence. It retains bounded diagnostics and
actual component/source hashes without credentials.

Full Container Smoke runs the queue mode after ordinary telemetry acceptance,
with a five-minute step timeout. Its `telemetry-queue-contract/` diagnostics join
the existing seven-day engine artifact, including on failure. Docs-only,
addon-only and stable-promotion paths are preserved. No production rollout,
new service, permission, credential transport, Archive change or game session
is required. Human gameplay acceptance remains separate.

This is a three-record recovery check with small test limits. It does not
establish production throughput or a prolonged-outage SLO.

## Local Verification

The final queue-mode run passed all three cases: observed depth two with one
retained native record, restart/drain with source sequences 1-3 and three inbox
rows, then one retained conflict across restart with two other rows delivered.
Both Reader projections matched. The original three delivery cases also passed
unchanged as a separate regression run. Each execution used a fresh disposable
PostgreSQL database, queues and synthetic credentials.

The native inputs and compiled fixtures were independently accepted artifacts
from main CI `43878593103`, revalidated against the current unchanged native
sources and lock. The native engine was not rerun locally for this slice.
Ten negative admission variants, seventeen altered queue-receipt controls,
explicit-mode rejection and the existing producer-contract checks passed.
CI fast-path and Markdown/link checks passed.

The complete local Quality Gate passed: audited restore, verified formatting,
build with zero warnings/errors, 2,323 passing tests and no reported vulnerable
packages. Seventy opt-in browser tests were skipped; this slice changes no UI.
Pinned InspectCode 2026.2.3.1 completed at Hint severity with zero errors and
zero warnings. Twelve reviewed layout hints remain intentionally: eleven
trailing-comma preferences conflict with enabled MA0007, and the explicit
`ConcurrentQueue.ToArray()` keeps its snapshot operation visible. No settings,
severities or suppressions changed. Inspection covered the changed test project;
the unchanged API/agent references were used as built assemblies.

Hosted acceptance is recorded below. No production rollout or stable promotion
is claimed.

## Hosted Acceptance

| Stage | Exact source | Full CI |
| --- | --- | --- |
| PR #559 | `a3d2e02` | [Run 43897559501](https://github.com/tov-vl/gold-src-ops/actions/runs/43897559501) |
| Post-merge main | `e6babbf` | [Run 43901851385](https://github.com/tov-vl/gold-src-ops/actions/runs/43901851385) |

Both first-attempt full runs passed Quality Gate, native compilation, client
policy, browser and Container Smoke, including the original telemetry cases and
the explicit queue recovery step. No manual rerun or protection bypass was used.

Independently downloaded fixture and engine ZIPs matched their provider SHA-256
digests. Each retained set passed source/fixture/lock verification, 26 native
cases / 55 markers, five precise native failure controls and owned cleanup.
The baseline producer bridge and original three HTTP-delivery cases remained
accepted. Queue receipts matched all three native input hashes and every
committed acceptance source-file hash.

Each queue run proved capacity two with two queued records and one unchanged
deferred record, retained identity/payload/sequence after restart, three accepted
inbox rows and a matching Reader projection. A separate server/source delivered
two other records while retaining one conflict with a single attempt. The
conflict remained unchanged after new import/delivery cycles completed, with
no automatic replay. Five rows were persisted across the two synthetic servers.

Separate independent local replays of both runs' downloaded native inputs also
passed in fresh disposable databases with new identities. API/agent/harness
assembly digests are recorded by CI; executable DLLs are outside the diagnostic
archives, so this is not an independently verified executable release. Archive
observation, production behavior and the human gameplay pilot remain separate.
