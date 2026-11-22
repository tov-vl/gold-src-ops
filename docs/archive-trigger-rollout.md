# Archive External Trigger R3 Delivery Preparation

Updated on 2026-11-22. Component 2.132.0 is stable from accepted candidate
`archive-trigger-v2.132.0-rc.4`.
The approved retained-ledger R3 passed controlled and two natural Archive slots
with independent Recovery, observer/Operator acceptance and final runtime proof.
The external timer is enabled and legacy scheduling is disabled. Prior rc.2/rc.3,
the previous Verified claim/history and all acceptance objects remain preserved.
The [seven-day assessment](archive-trigger-progress.md#seven-day-assessment-on-2026-11-22)
and [stable publication](archive-trigger-publication.md#stable-release-on-2026-11-22) passed.
This document does not authorize new access.
The [design](archive-trigger-decision.md), [implementation boundary](archive-trigger-implementation-plan.md)
and [verification progress](archive-trigger-progress.md) remain applicable.

## Frozen Runtime Scope

One external oneshot service and timer, one dedicated non-login service identity,
one protected token source and a read-only observation projection on the existing
control plane. GitHub admission helpers change with the writer workflow; the
accepted exporter pin and B2/Grafana pairs remain in their existing environment.
API and Web add the bounded automatic-origin projection on the existing Archive
health surfaces. Their candidate and rollback digests join the R3 manifest.
No game service, database or probe change is included; the other thirteen
containers remain outside this rollout.

The exact accepted product commit, package digest, live previous observer files,
systemd version and timer state must be recorded in the private operator manifest
after product CI and before rollout. Current working-tree review packages cannot
be installed as signed release candidates. The [component publication path](archive-trigger-publication.md)
uses separate `archive-trigger-v*` tags and the full CI gates. It binds the
clean, accepted revision and package checksum, rejects replacement and promotes
accepted candidate assets without rebuilding. Preparing/merging that path does
not authorize creating a candidate or stable tag or installing the service.
API/Web images follow the signed application candidate/stable flow for the same
release revision. Independently verify their digests and preserve prior image
slots and environment bytes before changing them.

## Management And Files

The package contains `archive_trigger_service.py` plus the state, transport and
workflow helpers, the read-only observer/publisher dependencies, unit files and
their tests. Installation must verify its manifest and immutable package digest
before copying code. Package code is root-owned, directories 0755 and ordinary
files 0644. The service cannot update its executable package or configuration.

The prepared service uses `Type=oneshot`, a 90-second hard unit limit, 85-second
process alarm, `Restart=no`, an empty capability set and a 128 MiB/16-task cap.
The timer reconciles every five minutes and also wakes at UTC `:53`. Persistent
wake-up coalesces missed times; the ledger admits only the newest eligible hour.
No backlog of writes is dispatched. The OS lock spans credential loading,
claim admission, POST, reconciliation and projection. SQLite commits Attempting
before the network call. Killing the process does not erase that state.

Management commands are fixed: `initialize`, `status`, `run`, `check-credential`. Initialize requires
paused configuration and a nonexistent ledger. It never recreates an existing
store. Status reads state without loading a token or changing claims. Run in
paused mode admits no new write but may reconcile an already Accepted run.
Unknown/Attempting block every generation; no reset or resend command exists.
The unit runs `check-credential` as `ExecStartPre`: it validates the mounted
credential without opening the ledger or creating a network client and prints
only a fixed success marker. First run the unit with paused configuration and
the timer disabled to accept the actual credential mount before admission.

Service configuration has a strict schema and begins paused. Fill generation,
activation time and the last independently verified Archive output end at rollout.
The generated template has intentionally invalid placeholders. Validate the
exporter pin, matching GitHub generation/mode and three helper content hashes.
The program also checks the installed admission helpers against those hashes.

Use separate ledger and sanitized projection directories (0750), both owned by
the service identity/group. The ledger database and lock are 0600. The observer
drop-in adds the service group as a supplementary group to read the 0640
projection; its parent grants group traversal only. This is necessary because
the existing root-run observer has an empty capability set. Explicitly mask the
ledger and token source/mount from that observer. Projection replacement uses
file/directory fsync. The observer gains no writable state or token access.
Include this group-based read permission in the R3 approval. Unknown state
publishes an invalid projection rather than fresh success.
Missing projection remains Unknown/ObservationInvalid.

Retirement is bounded to 100 old terminal claims/pass. During the initial
assessment, all claims since activation remain protected. After preserving and
closing that assessment explicitly, `assessment_closed=true` permits the normal
21-day terminal retention. Unresolved claims never expire. Full/corrupt state,
clock regression and catch-up beyond 24 hours stop admission. A slot already
covered by the accepted baseline is not exported again.

## Credential Boundary To Approve

One new fine-grained GitHub token, for `tov-vl/gold-src-ops` only, initially
90 days, with Actions write and mandatory metadata read. That authority includes
other Actions operations in this repository; the program implements only one
fixed workflow POST and bounded reads. Public repository content reads do not
justify adding Contents write, administration, secrets, workflow editing or
classic `repo` scope. Confirm provider compatibility with the scoped token before
activation; an unexpected permission requirement is a new decision.

The root-only token source is consumed through systemd `LoadCredential`; the
program reads the credential mount identified by `CREDENTIALS_DIRECTORY`.
The source file is root-owned 0600; its parent is protected. The unit masks that
original path from the service after credential loading. Only the mounted copy
is read by the service identity. The general protected-file reader remains
strict. A separate systemd-only reader requires the fixed credential directory,
a read-only filesystem, descriptor-bound metadata, no symlinks/hard links, a
regular bounded file and valid token text. It accepts service-owned private
read-only files without an ACL, or root-owned files with the exact service-user
ACL: owner read, named service UID read, owning group none, mask read, other none.
The directory has the matching read/execute ACL. Additional principals,
permissions, writable mounts or unexpected ownership fail closed. Root ownership
and group-class ACL mask bits are normal systemd behavior; they do not grant the
owning group access. Token values never appear in arguments, environment values,
logs, the package, projection or ledger. Python retains the value in process
memory until exit; no claim of guaranteed memory erasure is made.

The exact protected destination, existing approved SSH target and fingerprint
belong in the private supplement. The prepared
`tools/release/set-archive-trigger-credential.py` reads one hidden value and sends
it on stdin to the packaged root-only store helper through that approved alias,
with batch mode, strict known-host checking and port forwarding disabled. It
never writes a local token file. The remote helper uses exclusive creation and
fsync, never overwrites or automatically retries, and emits a fixed receipt.
Verify file metadata without reading the token back. No placeholder token may
be written to production. Record ID, expiry and rotation owner separately from
the secret. Initial creation/transport and service identity need explicit owner
approval before execution.

## Ordered Installation And Cutover

For an explicitly approved recovery, freeze the known existing installation,
identity and complete retained ledger in a fresh plan instead of repeating
initial provisioning. Completed claims remain recorded across generations;
never reinitialize the ledger or substitute an empty before-image.

1. Close source review and required CI; freeze the accepted candidate commit,
   signed component artifact and verified manifest. Run the clean-revision
   builder; review mode is not a production artifact. Retain current observer
   executable/configuration/unit bytes and repository mode/flag values privately.
   Extract the actual ZIP and check observer imports in isolated Python without
   checkout/PYTHONPATH dependencies or network access. Include the transitive
   `ops/gameserver/agent_diagnostics.py` contract used by the packaged transport.
2. Perform read-only host preflight: Linux/systemd credential support (minimum
   systemd 252), Python/SQLite runtime, available disk/RAM, host clock, existing
   identities/paths and no conflicting units. Unexpected existing installation
   or identity is a stop, not permission to overwrite it.
3. After approval, create the dedicated identity without shell/login, protected
   state/config directories and immutable package directory. Install both units
   disabled. Populate a root-owned paused configuration, then run initialize and
   status as the service identity. Do not initialize through the timer or reset
   state on rerun. Validate ownership, unit sandbox and package hashes.
4. Install and accept API/Web before publishing the additive origin metrics.
   Verify Operator authorization, legacy responses with Unknown origin and the
   unchanged eight-source report. Then provision the approved token through
   hidden input and existing SSH. Verify
   only metadata; run one bounded GET credential check and a dedicated no-write
   probe through the existing workflow. Keep probe claims/evidence separate from
   production admission state and confirm no source/B2 step ran. Do not discard
   uncertain probe state or repeat its POST. Provider response IDs and receipt
   log-host compatibility remain real target gates.
5. Pause new admission and disable legacy scheduling. Inspect both first-attempt
   jobs and queued runs; stop on uncertainty. Set the matched generation and
   external owner only after the old path drains. Configure the observer's
   existing unit with a reviewed drop-in using `publish-external`, projection
   path and generation; retain its other settings. Mask token source/mount and
   the ledger from that observer. Explain its automatic-origin semantics in the
   existing Operator view/report before considering cutover accepted.
6. Enable/start only the trigger timer. Accept one controlled write, perform
   independent reader Recovery of its exact object and verify two natural hourly
   slots. Compare claims, exact windows, immutable object receipts and observer
   times. A dispatch 200 or green probe is insufficient. Test warning routing
   only with isolated synthetic evidence, not by stopping real export.
7. Promote identical accepted bytes and close the evidence PR. Assess the
   three-hour age objective and requested-window coverage over seven days
   separately. Preserve source missing minutes and old uncovered intervals.

## Stop, Reconciliation And Rollback

Stop the timer first. Let an active oneshot finish, or preserve Attempting if it
was interrupted. Record paused admission to prevent new work. Status never clears
a claim. Paused run can poll an Accepted ID; a missing ID or unknown PUT requires
the exact-object/read-only investigation described in the design. No automatic
search, claim override, repeated POST or PUT is implemented.

Only after no outstanding remote write remains may the prior legacy
workflow/observer and schedule-enabled value be restored. Remove only the new
observer drop-in and restore verified prior bytes, preserving unrelated service
configuration. Restore only prior API/Web digests and exact environment image
slots. Verify one automatic owner. The disabled identity, ledger,
projection history and all created objects remain; token retirement requires its
own exact decision. No rollback deletes B2 objects or rewrites claim history.
If an unknown attempt cannot be reconciled, leave admission paused and report
the incomplete rollback rather than enabling cron beside it.

## Accepted Candidate And Stable Publication

The signed rc.4 package passed required PR/main/candidate CI, independent ZIP and
isolated extracted-package verification. The approved R3 accepted the real
credential mount, reused API/Web, no-write probe, controlled Archive, external
observer, Operator overview/detail/eight-source report and two natural hourly
slots with independent reader Recovery. The existing ledger was retained rather
than initialized again. See the [acceptance record](archive-trigger-progress.md#rc4-candidate-and-r3-acceptance).

The external timer is the sole automatic owner; legacy scheduling remains off.
The previous packages, credential/identity, Verified history and objects remain
preserved. The seven-day assessment verified 168 of 168 expected slots.
[Stable publication](archive-trigger-publication.md#stable-release-on-2026-11-22)
used the same source revision and the accepted candidate assets. The full
observation establishes the age and coverage results over the trial interval.
