# Paired Player Data Durability Rollout Plan

Status: R3 preparation, 2026-11-18. Read-only host inventory and local input
verification are complete. This is a design for the next delivery slice, not an
executable rollout bundle or production acceptance. No game restart, data write,
backup, publication or installation was performed during preparation.
The [paired installer](paired-player-storage-installer.md) now implements the
local tooling boundary; concrete hooks, publication and host acceptance remain
separate execution gates.

The [implementation](player-data-durability-implementation.md) has full PR/main
CI and independently downloaded-artifact evidence. The
[durability contract](player-data-durability-plan.md) remains zero lost
acknowledged individual saves after a process crash, without cross-vault
transactions, power-loss protection or a maximum-latency SLA.

## Frozen Inputs And Read-only Findings

- Accepted main: `8bc098ade231e3d08495f2793e1b99b9caa225b9`, after
  [PR #566](https://github.com/tov-vl/gold-src-ops/pull/566) and full
  [main CI](https://github.com/tov-vl/gold-src-ops/actions/runs/44156613587).
- Selected production nVault SHA-256:
  `e6a24a81b205410e9b5eb62ca3b1e2bb8f49a0480d5e84b177d7a57c512a0af6`.
  Use the production binary, never the testing binary with fault/timing controls.
- Selected addon 0.40.0 SHA-256 from that downloaded main run:
  `71aef4ec88c1f97528b749ab9d0e3111c28fe8db981e0742b2313f06d9741848`.
  Its source and include hashes match the accepted tree. The earlier local
  compiled addon differs; its package manifest must not describe these bytes.
  Reproducible native builds do not establish byte-reproducible Pawn builds.
- Runtime status reported addon 0.39.0. Its preserved package matches the
  installed receipt and payload. The stock nVault binary matches the pinned
  upstream archive; its loaded device/inode matches the file on disk.
- All three installed Pawn plugins have parsed native tables. Only the weapon
  addon imports nVault natives. A bounded scan of installed modules found no
  other `GetVaultMngr` reference outside nVault. This is an inventory check,
  not proof against every possible dynamically resolved native consumer.
- All 26 static baseline rows matched. No pending game upgrade/backup operation
  was found. The vault directory has the expected ownership and mode 0700;
  its only entries are the two canonical vault/journal pairs. Vault files are
  below all proposed limits. No retained checkpoint or partial journal was found.
- A2S reported zero players and bots at query time. This observation is not a
  reservation of the server. Recheck occupancy immediately before every stop.

Exact private paths, service identity, file sizes, timestamps, receipts, baseline
bytes, predecessor inputs and hashes are retained in the operator record.
Live inventory is not a closed backup. Kernel and service metadata do not prove
that the required filesystem operations work under the target service policy.

## Boundary And Tooling Gates

The future R3 bundle changes only the weapon addon, its paired nVault module,
the installation receipt and the reviewed backup baseline entries. Existing
configuration, loader and dictionary bytes are retained. API/Web, the agent,
Archive, backup schedule, credentials, firewall and other plugins are outside
the slice. The game must stop and start; the agent must retain its invocation.

The current standalone package deliberately has
`productionInstallSupported=false` and `requiredNvaultProcessContract=1`.
Do not change that flag or copy the module around the old installer. Existing
baseline tooling treats module replacement as unrelated drift. Before rollout:

1. Implement a dedicated paired manifest and verifier binding the addon,
   production module, native contract, corresponding source, unchanged payloads
   and exact predecessor. Reject a stock/missing module, testing binary,
   mismatched addon, unexpected path, untrusted link or unknown receipt schema.
2. Extend the guarded coordinator and baseline transition for this pair. Freeze
   expected before/after receipts and baseline bytes from reviewed artifacts;
   never learn the expected baseline from a changed host or waive unrelated
   drift. Preserve existing schedule/backup/transition lock order and pending
   protection so scheduled capture cannot observe a half-installed pair.
3. Journal each phase and its exact input hashes with a single-attempt claim.
   Handle refusal before mutation, completed failure, partial pair installation,
   completed baseline write and uncertain stop/start/write separately. An
   interrupted command requires reconciliation, not automatic replay.
4. Add a bounded standalone Linux i386 target-storage probe from the reviewed
   component source. It must use synthetic files in a private scratch directory
   on the target filesystem under the service identity, with no fault hooks in
   deployable code. Verify create/checked commit/checkpoint/rename/reopen and
   mode compatibility. It must never open live player vaults or kill the game.
5. Rehearse forward installation, each partial-write boundary, rollback and
   uncertainty handling in a disposable host, including actual backup guards.
   Run focused operational tests and complete PR/main CI for the new tooling.
6. Publish and independently download a signed, immutable paired candidate with
   source/licenses and the probe. The current CI artifacts alone are not a
   signed release. Reuse verified selected payload bytes; if they change,
   repeat the affected artifact acceptance and freeze new identities.

The new module creates runtime files as 0600 rather than the existing 0640.
The service owner can read them and root capture can copy them; the rehearsal
must prove real capture/restore and predecessor reopen without broadening ACLs.
Static module/plugin file metadata must follow the existing installation policy.

## Future Execution Sequence

Freeze the reviewed paired tool/candidate versions, exact host predecessor,
timeouts, acceptance commands and rollback artifacts in the operator bundle.
Obtain the R3 execution decision for that concrete bundle before any mutation.

1. Repeat read-only occupancy, consumer, mapped-module, baseline, pending,
   directory, space and service checks. Refuse drift, unsupported input bounds,
   unknown data files or any existing recovery remnants. Execute the synthetic
   storage probe within its approved scratch-only boundary. Gate native commit
   p99 at 2 ms and retain maximum latency as diagnostics. It does not establish
   the complete game-handler or frame-latency claim on this physical host.
2. Acquire the established locks and claim the operation once. Recheck empty
   A2S immediately before stopping. Record service identity, stop the game
   normally, require inactive/MainPID zero and no remaining engine process.
   Never use production SIGKILL as an acceptance technique.
3. Capture the two closed canonical vault/journal pairs using the existing
   backup format. Verify encrypted off-host upload and independent readback
   before replacing binaries. Prove both old/new readers preserve records and
   timestamps on private restored copies, not the live directory. Keep player
   values and identity keys out of public logs and evidence. If this gate fails,
   restart only the known unchanged predecessor after confirming its state.
4. Install and verify both binaries while the game remains stopped. Hash and
   metadata checks must cover both sides; replacing two paths is not one atomic
   transaction. Durable phase records must make a partial pair recognizable.
   Require byte-identical closed data throughout installation and a recognized
   receipt/baseline transition. Do not start a mixed pair.
5. Start once and require fresh 0.40.0 status, native contract 1, no storage or
   AMXX load errors, and the expected module mapped from the verified file.
   Require unchanged unrelated baseline rows, agent invocation and profile
   settings. Native checkpointing can change serialized bytes on startup:
   distinguish closed pre-start byte identity from post-start semantic equality.
   Prove the latter on private copies captured during a separately guarded
   empty-server stop/reopen; do not infer it from counts or live-file hashes.
6. Complete independent A2S/public status, static baseline, backup-guard and
   runtime acceptance. Use a controlled closed capture and off-host readback
   for the new format-compatible output. A second stop always needs a fresh
   empty-server check. Record terminal acceptance only after these gates pass;
   clear only this operation's matching pending marker and release locks.

Ordinary backup v1 contains four canonical files. It does not cover retained
checkpoint or partial-journal evidence. If such files appear, preserve them in
place, stop forward progress and plan a separate bounded evidence capture;
do not delete them, omit the anomaly or call the ordinary backup comprehensive.
The initial rollout does not silently change backup format or retention policy.

## Rollback And Stop Rules

Retain the exact installed 0.39.0 addon package, stock module, receipt and
baseline before mutation. A known completed failure permits the reviewed
rollback only when the actual pair and phase can be identified. Recheck that
the server is empty before stopping it for rollback. If a player arrives,
do not kick them or alter access policy; retain the operation and reconcile.

Restore predecessor binaries and compatible static metadata, preserving the
latest valid canonical player data. Prove predecessor-reader compatibility on
private copies before restart. Do not restore the preinstall data snapshot
automatically: that can discard newer saves and requires a separate decision.
Returning old binaries restores their weaker process-durability guarantee.
Rollback does not erase failure records, retained files or operation history.

Uncertain command delivery, unknown mixed installation, data corruption,
unexplained storage errors, unexpected consumers or a failed semantic check
stop the rollout. Do not clear pending state, repair data or repeat an install
to obtain a green result. Record service state and hashes for reconciliation.

## Completion Gates

Preparation is complete when inputs, read-only findings, risks and the tooling
gap are recorded. Execution readiness additionally needs the reviewed paired
coordinator, bounded probe, failure/rollback rehearsals, green full CI,
independently verified immutable publication and a fresh execution freeze.

Production completion additionally needs closed backup/off-host Recovery,
bounded host acceptance and final sanitized evidence. Stable promotion must
reuse the accepted payload bytes. Human gameplay, power-loss durability and a
physical-host maximum-latency SLA remain separate claims.
