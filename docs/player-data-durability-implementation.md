# Player Data Process Durability Implementation

Status: implementation, hosted CI and independently downloaded-artifact
acceptance complete, 2026-11-18. Production installation and signed release
remain pending. The
[original failed crash check](player-data-crash-acceptance.md) remains historical
evidence; this work implements the separately approved
[durability plan](player-data-durability-plan.md).

## Contract And Implementation

The supported loss budget is zero lost **acknowledged individual saves** after
process SIGKILL. An interrupted save can be present without acknowledgement;
it must not be automatically repeated. One death handler spans several saves
and vaults and is not a transaction. A crash inside it can leave a partial event.

The separate [Linux i386 nVault component](../native/nvault-durability/README.md)
pins AMXX 1.10.0.5481 source and AMTL, preserves the existing AMXX interface and
closed vault/journal formats, and exposes contract version 1 through
`nvault_process_version` and `nvault_process_commit`. A successful commit requires
complete append, checked flush and no earlier sticky write error. Errors poison
the handle; successful in-memory lookup cannot clear them.

Open and close serialize a bounded checkpoint to an exclusive file in the same
directory, check write/flush/close, parse and compare it independently, then
publish with atomic rename. Only after publication can the old journal be
removed. Replaying the old journal after another crash is idempotent for the
supported operations, including timestamps. A partial trailing append is retained
before complete preceding operations are checkpointed. Invalid records fail
closed. Interrupted checkpoint files are retained without overwriting history.

The source-only addon version is 0.40.0. Stats, preferences, streak records, HUD
and remembered names now require commit before readback and acknowledgement.
Failure invalidates the affected saved views for all connected players while
connection/map state continues. Promotion and record notifications require a
successful save. Storage recovery occurs on a fresh map load, not by retrying
the failed save. The saved leaderboard index is loaded after native recovery.

The addon requires the new native contract. The existing standalone installer
cannot safely supply that dependency; new addon package metadata deliberately
sets `productionInstallSupported=false` and declares
`requiredNvaultProcessContract=1`. Shipping it alone through the old installer is
blocked. Old production binaries and data remain unchanged.

## Build And ABI Gate

The original pinned module was rebuilt twice with identical bytes before addon
integration and loaded into the pinned ReHLDS/AMXX/ReAPI isolated engine. The
patched module is also built twice and compared byte for byte. ELF32/i386,
AMXX exports, upstream/source hashes, compiler package versions and the absence
of fault/timing hooks in the production binary are checked. The package includes
corresponding source and upstream licensing material.

An initial patched build exposed a real ABI incompatibility with the engine's
bundled older `libstdc++`. The accepted recipe statically links the C++/GCC
runtime; it does not replace any game-image library. The reproducible original
hash in the lock belongs to that final recipe, not to the initial dynamic build.
The final recipe also uses 64-bit file metadata to support large inode numbers
in a 32-bit process. Windows nVault support is not claimed.

The accepted production module SHA-256 is
`e6a24a81b205410e9b5eb62ca3b1e2bb8f49a0480d5e84b177d7a57c512a0af6`.
This identifies the independently downloaded, uninstalled CI artifact, not a
signed release.

## Isolated Evidence

All data is synthetic. Engine containers have no network and no published
ports; runtime scripts and compiled fixtures are copied and hash-bound before
execution. Editing the checkout cannot change a running acceptance script.

- The 36 native cases exercise two consecutive real SIGKILL events at each of nine
  checkpoint/recovery transitions, followed by independent reopening.
- Checked short write, flush, close, create, verify, rename, erase and open
  failures preserve earlier committed records and reject further acknowledgement.
  Real full tmpfs and readonly-path cases complement injected failures.
- Bounded malformed inputs, symlinks, size/entry/retention exhaustion and an
  invalid named pipe are rejected. 1,296 operation sequences check replay
  idempotence. Plain and Address/UndefinedBehavior Sanitizer runs are separate.
- The production binary passes actual engine baseline, write, SIGKILL, reversed
  player slots and second reopen. Saved totals, rank threshold, language,
  loadout, HUD, streak and name are checked. Removing the two pending synthetic
  journals makes the exact restored-state assertions fail.
- A separate testing binary covers commit refusal in each of five addon save
  paths. No false saved promotion or record is emitted. Its fault controls and
  timing natives are absent from the production binary.
- The full existing 26-case gameplay regression passes with the new contract.
  Evidence validators have 17 engine, six crash and four helper cases after the
  timing-gate follow-up.
- Both closed-format backup directions are checked through the existing
  `player-data-bundle.py` transport: old writer/new reader and new writer/old
  reader. Original input files, timestamps and absent-journal state are preserved;
  each restored copy is reopened again with the writer.

The 10,000-event, 32-identity synthetic handler benchmark measured p99 7 us in
the isolated run, below the proposed 5 ms gate. The native 10,000-commit test
also meets its 2 ms p99 gate. Injected 5 ms I/O correctly misses that gate while
preserving acknowledged values. These are page-cache/process measurements;
network rendering, real server frame latency and target-disk performance still
require host acceptance.

Both addon package modes pass the 121-case dictionary/boundary smoke and
source-dependency checks. Python/PowerShell parsing, shell syntax, YAML parsing,
change-scope selection and CI fast-path/failure-propagation checks pass locally.
No C# code changed; the .NET suite and InspectCode were not run for this slice.
Hosted full CI and downloaded-artifact results are recorded separately below.

Intermediate failed attempts are retained separately and excluded from accepted
evidence: incompatible C++ runtime, incorrect fake-client name provider, source
package license path, and a runner output filter that omitted new success
markers. A regression affected by editing a live mounted shell script was
rejected and repeated with frozen runtime inputs.
The cross-version backup test also exposed 32-bit file-metadata overflow on
large inode numbers. After selecting stat64, the same unchanged restored bytes
pass; checks on type, links, bounds and modification timestamps remain enabled.

## Hosted CI And Independent Artifacts

[PR #565](https://github.com/tov-vl/gold-src-ops/pull/565) accepted the implementation.
Its first main run exposed an additional maximum-latency assertion despite a
passing handler p99. The owner retained the 5 ms p99 gate and maximum latency as
diagnostics in [PR #566](https://github.com/tov-vl/gold-src-ops/pull/566).
Full [PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/44152036159) and
[main CI](https://github.com/tov-vl/gold-src-ops/actions/runs/44156613587) passed;
accepted main is `8bc098ade231e3d08495f2793e1b99b9caa225b9`.

The downloaded archives passed transport/source/payload checks and independent
native, crash, compatibility and gameplay evidence verification. A separate
replay of downloaded main inputs passed the benchmark and all five addon
save-failure paths. Main measured handler p99 8 us and maximum 1,586,852 us;
the independent replay measured p99 9 us and maximum 756 us. The cause of the
isolated tail observation is unknown. The original failed run and its evidence
remain retained. No maximum-latency or physical-host SLA is established.

The independently verified main addon binary is selected for paired delivery.
It differs from the earlier locally compiled addon despite matching source
hashes. Keep its own fixture manifest and exact bytes; do not reuse the local
standalone package manifest or claim reproducible Pawn compilation.

## Limits And Remaining Gates

This is process durability, without `fsync`, power-loss, kernel failure, disk
failure or arbitrary bit-rot guarantees. The legacy format has no checksum.
The design has a single writer, maximum 16 MiB vault, 4 MiB journal, 65,536
entries/operations and eight retained orphan checkpoint slots. Bounds or
retention exhaustion make storage unavailable without deleting history.

The [paired R3 preparation](player-data-durability-rollout-plan.md) records a
read-only inventory of installed consumers, baseline, file sizes, ownership
and loaded module identity, plus verified exact predecessor inputs. The
[dedicated paired installer and probe](paired-player-storage-installer.md) are
implemented locally. Their full CI, concrete owner hooks, immutable candidate
publication, physical target-filesystem probe, closed backup/off-host readback
and host acceptance remain pending. Returning old compatible binaries restores their weaker
guarantee; restoring an older backup is a distinct data-loss decision.

Local tests alone do not establish downloaded-artifact acceptance. The completed
CI/artifact checks do not establish physical-host or human-player acceptance.
Archive's observation window and the human pilot remain independent.
