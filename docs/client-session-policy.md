# Same-user Windows session policy

Status: the owner completed the one-time local policy bootstrap on 2026-11-06.
Fresh restricted SSH status confirmed the existing updater and worker remained
healthy. The signed runner rollout, changed-session exercise and native trial
are still pending; a policy-install receipt is not acceptance of those gates.

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
claims and retained-failure rejection. Live session change, task execution,
renderer output and locked-desktop behavior remain target gates.
