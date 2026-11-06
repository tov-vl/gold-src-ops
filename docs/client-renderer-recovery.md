# Retained renderer-log recovery

Status: the owner approved one bounded recovery and fresh native trial on
2026-11-06. Target execution and independent acceptance remain pending. The old
renderer result remains failed and is not accepted retroactively.

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
