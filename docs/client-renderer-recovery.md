# Retained renderer-log recovery

Status: the owner-approved bounded recovery and one new EN native trial passed
independent acceptance on 2026-11-06. The old renderer result remains failed
and is not accepted retroactively.

An existing console log disables renderer probing. A separately owner-approved
recovery can move exactly one retained root `qconsole.log` into a unique sibling
archive on the same volume. The plan binds an operation ID, old job ID, SHA-256,
length, creation/modification times and the original game PID/start/completion
times. It cannot carry a path, command, target identity or alternative filename.

`New-LabBundle.ps1 -RendererRecoveryPlanPath <private-review.json>` embeds this
bounded review in the Worker through the existing signed update. The default
empty plan performs no operation. Only Worker and Diagnostics need replacement;
privileged updater/common code, policy, keys, task definitions, SSH command set
and access settings remain unchanged. No local installer is needed.

The authorized limited-user worker requires an idle, observable client, terminal
request results, no ambiguous console log, matching failed-probe bindings, exact
source metadata/hash and a valid nonce frame. It refuses linked paths and an
existing archive. A create-only operation claim and receipt prevent automatic
repetition. The rename does not overwrite a destination; readback must prove
the archive hash, absent original path and unchanged old result. No old result
is upgraded to successful acceptance.

An uncertain move remains blocked, with its available data and claim retained.
There is no automatic reverse move or retry. A code rollback does not restore
the old log location; the archive survives rollback. Any reverse move needs a
new exact decision and an empty original path. A later newly created console
log is not moved again under the old receipt.

Before target execution, freeze the concrete private review, signed runner
identity, rollback inputs and one fresh trial. A new native job is permitted
only after independently verified archival and healthy worker status. The new
job needs its own renderer receipt, reviewed BMPs and process/config/log cleanup.
A real Windows session transition is independent of this recovery.

Local coverage includes disabled behavior, review shape and timestamps, changed
hash/creation/modification time, wrong nonce/PID/lifetime, active game or request,
ambiguous logs, existing destination, pre/post-move uncertainty, retained-result
preservation, missing receipt/used claim and no-retry behavior. No target side
effect follows from passing these local tests alone.

## Delivery and target evidence

[Product PR #485](https://github.com/tov-vl/gold-src-ops/pull/485) accepted source
`f67c39ce495e44a180b5bc8b96188a5d9da70f69`, merged as
`30f671ad7954a24de891b59118b07ae7071b3c8f`. All six required checks passed in
[PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/42193068500) and
[main CI](https://github.com/tov-vl/gold-src-ops/actions/runs/42195113444).
The full client suite passed in PowerShell 7 and 5.1, including 68 recovery
checks over 18 scenarios in each runtime, native compilation, 482 synthetic
native checks and seven Python fixture-policy tests. No C# source changed;
InspectCode and a duplicate application-wide local gate were not applicable.

One signed sequence-29/revision-16 update installed Worker and Diagnostics while
preserving the other seven files. Independent restricted readback matched all
nine hashes. The exact retained 2877-byte log was renamed to its reviewed unique
archive, with matching SHA-256, absent source and unchanged old failed result.
A later ready worker tick rehashed the archive before native dispatch; the
archive and failed result were still preserved after the new trial. No local
installer, UAC prompt, queue drain or new access permission was needed.

One new EN `/profile` trial retained five reviewed 1920x1200 native BMPs. The
independent visual gate matched the accepted profile baseline and confirmed
both OK closes, alternate alias/cooldown and control return in both epochs.
No manual window selection was requested. A separate source-hash-bound
renderer receipt reparsed the fresh process-bound frame and identified Intel
Iris Xe with OpenGL. This describes the engine-reported identity, not hardware
acceleration, driver correctness or performance.

Owned game, generated CFG and new console-log cleanup passed. The disposable
fixture, relay and network were removed, no AMXX errors were found, and a fresh
client observation confirmed an idle ready worker. Raw evidence remains private
and unchanged; historical acceptance flags remain false. This closes only the
approved EN renderer gate, not a new RU matrix, real Windows-session transition,
saved-data/loadout expansion, two-player test or activity observation window.
Production services and the addon were unchanged.


## Automatic log inspection and EN weapons acceptance

The worker reports `rendererPreflight` through the existing SSH status while
its Windows session is valid. The report covers the four fixed `qconsole.log`
and `console.log` paths in the game root and `cstrike`. It includes presence,
size, timestamps and SHA-256 for regular files up to 4 MiB. Active games or
unavailable metadata block a clear-path conclusion. This report contains no
console text and works when historical renderer results are ambiguous.

The queue guard accepts a completed `rejected_preflight` result with false
cleanup confirmations only when the result proves that no launch was requested
or dispatched, no launcher or game process was created, no frames were captured,
and the temporary configuration was never created. Existing identity,
completion, cleanup and log checks still apply. The same preflight checks
drive recovery and its read-only diagnostic status. A blocked receipt stays
immutable. Its preflight can be observed again through status without moving a
file or reusing the claim.

On 2026-11-19, signed client-lab maintenance updates passed installed-file
readback. The read-only report identified a completed preflight rejection with
no process or configuration to clean up. After the narrow queue fix, all seven
queue entries passed. The authorized retained log was saved to a sibling
archive whose SHA-256 matched the source on a later worker cycle. All four
renderer log paths were clear before the game and after cleanup.

One EN `/guns` run on the isolated 0.40.0 addon and nVault fixture passed.
Two bots joined after the menu opened. Movement before the test death, respawn
with AK-47 and Desert Eagle, return of movement, and the server acknowledgement
bound to the fifth frame were confirmed. Five frames and four checkpoints
matched their recorded hashes, and the fixture reported zero AMXX errors.
A separate receipt bound to the same client result identified engine-reported
OpenGL. The game, temporary config, new console logs, fixture, network and
relay were cleaned up. Raw screenshots, logs and operational identifiers
remain in the private evidence record.

This run exercised the signed lab payload. Repository source and CI cover the
portable implementation. The evidence covers one EN client with bots and the
engine-reported renderer.
