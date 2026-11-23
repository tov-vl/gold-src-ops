# Native Gameplay Telemetry Acceptance

This repository-only slice joins previously separate native producer, agent,
PostgreSQL inbox and Reader checks. Local acceptance uses the three verified
natural objective records from the preceding engine acceptance. Full CI must
produce and accept its own engine records before running this check. Accepted
through [PR #557](https://github.com/tov-vl/gold-src-ops/pull/557), full PR/main CI
and independent downloaded-artifact verification on 2026-11-16.

## Executed Boundary

`game-event-telemetry-e2e.py` first checks the current engine lock, compiled
fixture manifest/source hashes, successful isolated engine result, all native
scenario markers and the three exact objective byte receipts. Missing, failed,
altered or incomplete evidence fails admission. There is no synthetic fallback.
The small generated inputs in the admission unit tests never enter runtime
acceptance.

The harness starts the actual API through `WebApplicationFactory` with Kestrel
on an ephemeral IPv4 loopback socket, production EF/Npgsql registration and
explicit migrations in a disposable PostgreSQL database. The database port is
also loopback-only, with a random host port and bounded CPU/memory. Unrelated
hosted workers are removed from this test host; application startup still does
not migrate production databases.

The real agent executable initializes a fresh SQLite queue and imports the
unchanged native spool envelope. Its ordinary delivery worker obtains a signed
JWT from a local client-credentials endpoint and sends HTTP through a bounded
fault proxy to the actual ingest endpoint. The API retains real JWT validation,
writer permission/server binding and Reader authorization. Keys and credentials
are ephemeral test values. The machine token is denied Reader access and the
Reader token is denied ingest access.

## Required Scenarios

| Scenario | Native input | Required outcome |
| --- | --- | --- |
| Ordinary delivery | C4 explosion | HTTP 202, one inbox row, drained queue, matching Reader projection |
| Lost acknowledgement after commit | C4 defuse | API commits and proxy aborts the response; restart preserves the queued identity, source sequence and payload hash; replay returns HTTP 200 with the original receipt and no second inbox row |
| Unavailable API and agent restart | Round timeout | Proxy returns HTTP 503 without forwarding; inbox remains unchanged; after restarting the agent and restoring forwarding, the original event is accepted once |

Each scenario owns a separate fresh queue. The HTTP payload hash must match the
imported queue payload on every attempt. The check compares source/event identity,
sequence, server binding, receipt, stored native occurrence/map/population and
the actual bounded Reader HTTP response. Across all scenarios PostgreSQL must
contain exactly three rows. Native connected-client semantics remain
`players=3`, `bots=3`; this is not evidence of human gameplay or activity.

The lost-ack check intentionally interrupts transport after the real API returns
202. The unavailable case injects HTTP 503 before the API, rather than stopping
a production service. Agent restarts terminate only processes owned by this
runner. It never edits queue state to bypass a lease, retry or dead letter.

## Reproduction And Evidence

From the repository root, with .NET 10, Python and a local Docker engine:

```sh
python3 -B tools/smoke/test-game-event-telemetry-e2e.py
python3 -B tools/smoke/game-event-telemetry-e2e.py \
  --evidence <accepted-engine-output>/evidence \
  --compiled <matching-compiled-fixtures> \
  --output <new-telemetry-output>
```

The output directory must be new. Runtime failures fail the command and retain
bounded diagnostics. Successful `result.json` binds native input hashes to the
three outcomes, exact receipts and observed status sequences. Temporary queues,
synthetic secret files and the disposable database are removed before success.
The acceptance result and bounded log remain available for review.

## PostgreSQL Snapshot Recovery

Run the same command with `--postgres-recovery` and a new output directory.
Use a matching, recent engine artifact within the agent's event age limit.
The runner binds the three native inputs to one SQLite queue and one server.
It takes a custom PostgreSQL dump after sequence 1 is accepted. Sequence 2 then
receives its own HTTP 202 and leaves the outbox. Sequence 3 stays in the outbox
at cutover. The runner stops the original PostgreSQL container, checks the dump
bytes after transfer and restores them into a second disposable container. A
fresh API host serves the restored database, then the agent resumes from
its existing queue.

| Event | State at cutover | PostgreSQL and Reader after delivery |
| --- | --- | --- |
| Sequence 1 | Present in the dump | Present once |
| Sequence 2 | Acknowledged after the dump and removed from the outbox | Absent |
| Sequence 3 | Still pending in the outbox | Present once, with matching identity and payload |

The runner checks two rows before cutover, one immediately after restore and
two after delivery. The Reader check compares the same counts and native
round fields against the real HTTP endpoint. The receipt includes the dump
SHA-256 and the three event outcomes. In this dump-only recovery scenario,
the agent can redeliver retained events. Recovering an event acknowledged
after the dump requires an independent source that retained it, such as
PostgreSQL WAL available for point-in-time recovery.

## Local Verification

On 2026-11-16 the three runtime scenarios passed using independently accepted
native artifacts from PR #555, revalidated against this checkout's unchanged
engine lock and fixture source identities. The unchanged native engine was not
rerun locally for this slice. The ordinary, lost-ack and unavailable cases
produced the expected `202`, `202 -> 200` and `503 -> 202` sequences, respectively,
with exactly one persisted event per input and matching Reader fields.

All ten negative admission variants passed, together with the seventeen engine
runner tests, existing producer-contract tests and 127 focused GameEvent tests.
The complete local Quality Gate passed: audited restore, verified formatting,
build with zero warnings/errors, 2,323 passing tests and dependency audit with
no reported vulnerable packages. Seventy opt-in browser tests were skipped;
this slice changes no UI and claims no new browser acceptance.

Pinned InspectCode 2026.2.3.1 completed at Hint severity with zero errors and zero
warnings. Its nine reviewed layout hints are intentionally retained: eight
trailing-comma preferences conflict with the enabled MA0007 rule, and the
explicit `ConcurrentQueue.ToArray()` keeps the observation snapshot operation
visible. No severity, suppression or inspection settings were changed. The
API and agent references were inspected as built assemblies; production source
was not changed. Hosted acceptance and independent downloaded-artifact
verification are recorded below.

Full Container Smoke executes the check after native engine acceptance and
retains `gameplay-engine/telemetry-contract/` with the existing seven-day engine
artifact, including on failure. Docs-only, stable-promotion and addon-only paths
do not run the .NET telemetry harness. The fast-path smoke checks this boundary,
the required timeout, fixture input and diagnostic retention.

## Hosted Acceptance

| Stage | Exact source | Full CI |
| --- | --- | --- |
| PR #557 | `11e2eed` | [Run 43874687617](https://github.com/tov-vl/gold-src-ops/actions/runs/43874687617) |
| Post-merge main | `e4267ab` | [Run 43878593103](https://github.com/tov-vl/gold-src-ops/actions/runs/43878593103) |

Both complete runs passed Change Scope, Quality Gate, Map Menu Compile, Client
Lab Policy, Container Smoke and Browser Smoke on their first attempt. The full
telemetry step executed in each run; no manual rerun or protection bypass was
used.

Independently downloaded fixture and engine ZIPs matched their provider SHA-256
digests. Each accepted set contained thirteen compiled fixtures, 26 native
scenarios / 55 markers, all five precise failure controls, three natural round
records and successful owned-container cleanup. The existing offline bridge
still imported ten baseline records and rejected five malformed controls.

Each telemetry receipt matched its own run's native byte hashes, fixture/lock
identities and ten exact committed source-file hashes. Ordinary delivery,
lost acknowledgement and unavailable/restarted delivery preserved identity and
payload, produced one inbox row per native input, drained the queues and matched
the Reader projection. Their ordinary status sequences were `202`, `202 -> 200`
and `503 -> 202`.

An additional independent execution of each run's downloaded native inputs
passed in a fresh local PostgreSQL database with new event/source identities.
It verified real agent delivery, API persistence and Reader HTTP results again.
The receipt records API/agent/harness assembly hashes, but executable DLLs are
outside the retained diagnostic archives; no independently verified executable
release is claimed. Archive observation and human gameplay acceptance remain
separate.

No shipped producer, schema, application contract, production identity, running
service, client laptop, Archive schedule or observation state changes. No release
tag or deployment is needed for this isolated acceptance tooling. Real-player
acceptance and the Archive observation window remain independent gates.
