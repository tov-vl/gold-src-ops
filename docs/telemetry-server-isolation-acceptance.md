# Parallel Telemetry Server Isolation Acceptance

This slice extends the existing [telemetry harness](native-telemetry-acceptance.md)
and [queue recovery acceptance](telemetry-queue-recovery-acceptance.md) with two
concurrent real agent processes. Each has its own spool, SQLite queue, source
identity and synthetic machine credential. Both use the same real HTTP/JWT API
and disposable PostgreSQL database. Production code and policy are unchanged.

## Missing Boundary

API integration tests already deny cross-server machine writes and filter
Reader history by server. PostgreSQL tests cover racing duplicate requests.
Queue recovery already covers concurrent import/delivery inside one agent,
capacity pressure, restart and retained conflicts. Those scenarios run
sequentially; they do not establish independent progress of two live agents.

## Controlled Cases

| Case | Controlled input | Required evidence |
| --- | --- | --- |
| Parallel delivery | Start both import/delivery processes, retain one input in each queue behind HTTP 503, then release both transports | Distinct source identities, two live processes, one fresh receipt per server and matching PostgreSQL/Reader rows |
| One server unavailable | Hold the first transport at HTTP 503 and admit a second native record to each agent | First event remains queued while the second server reaches two accepted rows before recovery is released |
| Cross-server forbidden | Send the first agent's captured valid request to the second server with the first server's actual signed JWT | Real API HTTP 403, unchanged three-row inbox, then a third healthy-server delivery while the first still retains its backlog |
| Recover and isolate | Restore the first transport while both original agents remain running | Six distinct rows total, independent sequences 1-3, retained payload/identity, empty queues/spools, completed idle loops and matching per-server Reader history |

Both servers use the same three verified native objective records in a different
order. Intermediate Reader checks have different counts and native content, so
cross-server history cannot pass merely because both final histories contain
the same inputs. The Reader role retains its existing ability to read servers;
this checks route filtering, not new per-user tenancy or Reader permissions.

Progress uses actual queue, request, persistence and worker-cycle checkpoints.
The outage remains active until the healthy server completes each required
delivery. This proves overlapping process lifetimes and progress isolation;
it does not require simultaneous PostgreSQL transactions or establish fairness,
throughput, prolonged-outage reliability or an SLO. Queue rows and receipts are
never changed to make a check pass. No dead-letter replay is added.

## Execution And Evidence

```sh
python3 -B tools/smoke/test-game-event-telemetry-e2e.py
python3 -B tools/smoke/game-event-telemetry-e2e.py --server-isolation \
  --evidence <accepted-engine-output>/evidence \
  --compiled <matching-compiled-fixtures> \
  --output <new-server-isolation-output>
```

The mode is explicit and mutually exclusive with `--queue-recovery`; the
original three delivery cases remain the default. Existing engine admission
validates source/fixture/lock identity, markers and native record bytes. The
new result validator checks all four cases, two-server/source binding,
independent sequences, retained queue payloads, native content, progress before
recovery and denial without persistence. Synthetic unit receipts only exercise
that validator and are never runtime evidence.

Full Container Smoke runs this mode after the preceding telemetry checks, with
a five-minute timeout. Bounded receipts and diagnostics join the seven-day
engine artifact, including on failure. Docs-only, addon-only and stable-promotion
routes remain unchanged. Assembly hashes are recorded, but diagnostic artifacts
are not an independently verified executable release.

## Verification Status

The local two-agent run passed all four cases with six persisted rows. Both
agents retained independent source identities and sequences 1-3. The healthy
server progressed before release of the first server's HTTP 503 outage and
after a real cross-server HTTP 403. The rejected request changed no inbox row;
recovery retained queued payloads/identities and both Reader histories matched.
The existing queue mode passed three cases / five rows, and ordinary delivery,
lost acknowledgement and unavailable-API restart passed three cases / three
rows as separate regressions. Temporary queues, credentials and databases were
removed after each run.

Local native inputs were the independently accepted artifacts of main CI
`43901851385`, revalidated against unchanged current native sources and engine
lock. No native engine was rerun locally for this slice. All seventeen current
acceptance source-file hashes are retained with each runtime result.
Twenty-six corrupted isolation receipts, seventeen existing queue-receipt
controls, ten engine-admission variants and explicit-mode rejection passed.
The existing producer-contract tests, CI fast-path and documentation/link
checks also passed.

The complete local Quality Gate passed audited restore, verified formatting,
build with zero warnings/errors, 2,323 tests and no reported vulnerable packages.
Seventy opt-in browser tests were skipped; this slice changes no UI. Pinned
InspectCode 2026.2.3.1 completed at Hint severity with zero errors/warnings.
Twelve reviewed trailing-comma preferences conflict with enabled MA0007 and
remain intentional. No settings, severities or suppressions changed. Inspection
covered the four changed/new harness files; unchanged API/agent references were
used as built assemblies.

Hosted acceptance through [PR #561](https://github.com/tov-vl/gold-src-ops/pull/561)
is recorded below. No production rollout, Archive change, provider
access, game profile change or client session is part of this acceptance. The
seven-day Archive assessment and two-human gameplay pilot remain open
independently.

## Hosted Acceptance On 2026-11-17

| Stage | Exact source | Full CI |
| --- | --- | --- |
| PR #561 | `5c2a5ca` | [Run 44040804025](https://github.com/tov-vl/gold-src-ops/actions/runs/44040804025) |
| Post-merge main | `f89f36a` | [Run 44043802107](https://github.com/tov-vl/gold-src-ops/actions/runs/44043802107) |

Both first-attempt full runs passed Quality Gate, native compilation, client
policy, browser and Container Smoke. The new server-isolation step and both
preceding telemetry modes executed successfully; no manual rerun or protection
bypass was used.

Independently downloaded fixture and diagnostic ZIPs matched their provider
SHA-256 digests. Each set passed source/fixture/lock verification, 26 native
cases / 55 markers, five precise native failure controls and owned cleanup.
The native producer bridge, three original delivery cases and three queue
recovery cases remained accepted.

Each new isolation receipt matched all three native input hashes and seventeen
committed acceptance source-file hashes. It proved two concurrent live agents,
independent queues/sources/sequences, healthy delivery before releasing the
other server's HTTP 503 outage, a real cross-server HTTP 403 without an inbox
write and progress after that denial. Recovery preserved queued bytes and
identities, produced six distinct rows and matched each server's Reader history.

Separate local replays of both runs' downloaded native inputs passed in fresh
disposable databases with new identities. Assembly digests are recorded by CI;
DLLs are outside the diagnostic archives, so this does not independently verify
an executable release. Production, Archive observation and the human gameplay
pilot remain separate.
