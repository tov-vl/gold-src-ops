# Player Data Process Durability Implementation

Status: local implementation and isolated acceptance, 2026-11-17. No production
installation, remote publication or release is included in this record. The
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

The locally accepted production module SHA-256 is
`e6a24a81b205410e9b5eb62ca3b1e2bb8f49a0480d5e84b177d7a57c512a0af6`.
This identifies an uninstalled local package, not a published release.

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
  Evidence validators have 17 engine, six crash and three helper cases.
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
Hosted full CI, including the existing engine negative controls, remains a
separate gate.

Intermediate failed attempts are retained separately and excluded from accepted
evidence: incompatible C++ runtime, incorrect fake-client name provider, source
package license path, and a runner output filter that omitted new success
markers. A regression affected by editing a live mounted shell script was
rejected and repeated with frozen runtime inputs.
The cross-version backup test also exposed 32-bit file-metadata overflow on
large inode numbers. After selecting stat64, the same unchanged restored bytes
pass; checks on type, links, bounds and modification timestamps remain enabled.

## Limits And Remaining Gates

This is process durability, without `fsync`, power-loss, kernel failure, disk
failure or arbitrary bit-rot guarantees. The legacy format has no checksum.
The design has a single writer, maximum 16 MiB vault, 4 MiB journal, 65,536
entries/operations and eight retained orphan checkpoint slots. Bounds or
retention exhaustion make storage unavailable without deleting history.

Before production: inventory every nVault consumer and actual file size/rate,
check ownership and Linux rename support, complete full hosted PR/main CI,
independently download and verify the production addon/module/source artifacts,
and replay them again. A separate R3 plan must supply paired installation,
closed backup/off-host readback, an empty-server gate, bounded host acceptance
and exact rollback. Returning old compatible binaries restores their weaker
guarantee; restoring an older backup is a distinct data-loss decision.

Local tests do not establish downloaded-artifact, physical-host or human-player
acceptance. Archive's observation window and the human pilot remain independent.
