# Same-user Windows session policy

Status: signed runner adoption, real same-user session change and one new EN
native trial accepted on 2026-11-06. The owner completed the local bootstrap
and later signed out/in; the runner followed the new session without another
installer or protected policy update. Windows/Steam login and an unlocked
desktop remain external prerequisites.

The legacy runner binds the worker to one numeric Windows session. The optional
`gameSessionMode=current-user-interactive` policy permits a newer runner to use
only its own current interactive session for the same fixed user SID. Absence
of this field retains `exact-session` behavior; unknown modes fail closed.
The effective session is copied into the in-memory policy and recorded in the
worker heartbeat and new job result. The protected policy is not rewritten at
every Windows login. The desktop helper must match the session of the job claim.

The process observation still requires exactly one Steam process with the fixed
path, owner and effective session, and no existing game process. Existing native
desktop, focus, PID/start-time and process ownership guards remain unchanged.
The mode does not log in, unlock Windows, change task principals or select a
different user's session. A locked or unavailable desktop is not acceptance
evidence, even if a worker heartbeat is present.

## Two-stage installation

1. Prepare a local package with `tools/client-lab/New-SessionPolicy.ps1` from an
   owner-controlled review record. It binds the target, user SID, existing
   policy digest, six trusted updater files, nine runner hashes, sequence and
   terminal request inventory. Private values stay outside tracked source.
2. The owner runs `Start-Session.cmd` once on the client and approves UAC. The
   installer preserves the original policy and updater configuration in an
   administrator-owned backup, disables existing tasks while writing, adds only
   the mode field and changes only the existing policy hash pin in the updater.
   It preserves signing keys, pinned runner dependencies, journal, tasks,
   firewall, SSH configuration, request results and retained logs. It does not
   launch Steam or the game. Installation refuses a changed predecessor,
   unfinished update, active game or unreviewed request.
3. Success is `policy_installed_pending_runner`, not native acceptance. The
   original runner still uses exact-session behavior. Install the separately
   verified Worker/Diagnostics through the existing signed-update path; the
   privileged common module and updater trust engine remain unchanged.
4. Inspect fresh remote status and exact code hashes, then separately verify a
   new interactive session and a bounded native trial. Existing uncertain jobs
   remain terminal claims and are never automatically replayed.

If an install fails after tasks are disabled, it retains rollback bytes and
leaves the tasks disabled for reconciliation. Do not rerun the package. Before
reverting to a legacy exact-session runner, confirm its configured session still
belongs to the fixed user; restoring old bytes alone is not session acceptance.

The bootstrap can tolerate one explicitly recorded renderer-log cleanup failure
only when game/config cleanup, original PID/start time, completion time and job
identity match the owner review. It checks request/result hashes for stability,
preserves that failed result, and neither archives nor deletes the log.

Local tests cover policy selection, foreign users, invalid modes, old sessions,
Steam ambiguity, exact policy/config deltas, backup/commit drift, one-attempt
claims and retained-failure rejection. Passing these tests alone does not prove
live session change, task execution, renderer output or locked-desktop behavior.

## Delivery evidence

[Product PR #483](https://github.com/tov-vl/gold-src-ops/pull/483) accepted source
`304b8e0c5cba0ed6a64b4231ffdaa94101e90ba4`, merged as
`9f9b6dff6da625543c705356f16a2760a55a20c8`. All six required checks passed in
[PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/42182748037) and
[main CI](https://github.com/tov-vl/gold-src-ops/actions/runs/42184626602).
The policy and bootstrap checks passed 23 and 39 cases per PowerShell runtime,
with 18 worker scenarios, the existing client suite, both native compilation
checks and 482 synthetic native checks. The worker fixture explicitly models an
interactive session independently of the CI host's session. C# source was not
changed; the application-wide local gate was not repeated.

One signed sequence-28/revision-16 update replaced Worker and Diagnostics and
preserved the other seven runner files. Independent restricted readback verified
all nine hashes, fresh ready status, the approved mode and the worker's current
session. The updater remained healthy; the configured legacy session was kept
for compatibility. No new remote permission or credential was needed.

The retained request inventory, original failed renderer result and bounded
retained-log report were independently unchanged. No game was dispatched, no
log was archived or deleted, and production was untouched. The local bootstrap
backup and exact previous runner bytes remain available to the owner.

That initial acceptance covered installation and execution in the existing
interactive session. It did not claim a real login/session-number transition,
unattended Windows/Steam login, locked-desktop operation or native renderer
acceptance. The failed renderer job must not be replayed.

The later separately approved [renderer recovery](client-renderer-recovery.md)
under PR #485 preserved that failed job and accepted one new EN native renderer
trial on 2026-11-06. It does not prove a real login/session-number transition
or operation on a locked desktop.

## Real changed-session acceptance

On 2026-11-06 the owner signed out and back into the same Windows account, then
left the desktop unlocked and Steam ready with a free account. Fresh restricted
status showed the worker automatically running in a different interactive
session while the configured legacy session number remained unchanged. The
installed sequence stayed 29/revision 16, all nine hashes matched, and prior
terminal results and the retained renderer archive were preserved. No runner
update, protected policy edit, UAC installer or new permission was needed.

One new EN `/profile` job recorded that observed session in `gameSessionId`.
Independent review passed five native 1920x1200 BMPs, the accepted profile
baseline, both OK closes, alternate alias/cooldown and control return in both
epochs without manual window selection. A separate source-hash-bound renderer
receipt identified fresh process-bound OpenGL on Intel Iris Xe. Owned process,
generated CFG and new log cleanup passed; the disposable fixture, relay and
network were removed without AMXX errors. Final status was fresh, ready and idle
in the changed session with the same nine code hashes. Old results and archive
were independently preserved after the trial.

This accepts the bounded same-user session-transition gate. It does not automate
Windows/Steam authentication, unlock the desktop, cover a different user, prove
hardware acceleration/performance or extend saved-data/loadout coverage. It adds
no production or addon change. No unchanged implementation suite was rerun;
validation used direct target observation, independent native/frame/renderer
review and changed-documentation checks. C# source is unchanged.
