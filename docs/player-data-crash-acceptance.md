# Player Data Process-Crash Acceptance

Local status on 2026-11-17: the strict preservation check failed and reproduced
loss of acknowledged post-checkpoint updates. This is a discovered durability
limitation, not completed crash-recovery acceptance. Product code is unchanged.

## Boundary And Coverage Audit

This slice checks the existing weapon addon's persistence after abrupt loss of
the game process. It changes fixtures, the isolated runner and CI only. The
shipped addon, vault schemas, backup mechanism and production configuration are
unchanged. No Steam account or human participant is required.

| Existing check | What it proves | Added boundary |
| --- | --- | --- |
| Persistent stats and preference seed/reload fixtures | Saved counters and settings survive a graceful process/map change | Updates after a clean checkpoint survive SIGKILL without plugin shutdown |
| Player-data bundle and engine recovery | Synthetic closed vaults survive capture, restore and engine reopening | Real nVault journals replay into the unchanged checkpoint |
| Multiplayer and native combat acceptance | Combat accounting, protection and independent connection state | Durable identity remains separate when client slot order changes after a crash |

The new fixture includes the unchanged product source. Only synthetic client
identities bypass bot filtering. It uses real nVault without lookup, write,
close or flush substitutions. Baseline seeding sets deterministic counters;
post-checkpoint scoring invokes the product's `RecordMapDeath` persistence path.
Settings use its save/readback helpers. This is a persistence check, not another
native-damage, menu-input, Steam-authentication or client-rendering claim.

## Required Chain

1. Create two synthetic identities with different kills/deaths, rank, language,
   primary/pistol choices and HUD preferences. Quit normally and retain both
   closed vaults as the baseline checkpoint.
2. Open that checkpoint in a new engine process. Verify the baseline before
   recording one opposing score event for each player and changing each
   player's settings. Require all product readbacks and the before-kill marker.
3. Identify the direct child engine through `/proc`, verify its executable and
   send SIGKILL to that process only. Require exit 137 and absence of the
   fixture's `plugin_end` marker. There is no restart supervisor.
4. Require both checkpoint vaults to remain byte-identical to the baseline and
   both journals to exist with more than a header. Retain these synthetic files
   and the exact signal/exit receipt in diagnostics.
5. Start another process with the player-to-slot order reversed. Require each
   identity's updated totals, rank threshold, language, loadout and HUD settings.
   Connection/map counters start fresh. Quit normally.
6. Reopen in the original slot order and require the same durable state again.
   This detects loss, identity mixing or replay that changes the accepted totals.

Each of the four process stages must emit its distinct marker exactly once
with zero failures. Missing/duplicate markers, runtime errors, wrong exit status,
graceful shutdown in the killed process, changed checkpoints or missing journals
fail acceptance. Only the three clean processes may emit `plugin_end`.

An additional deliberately broken run removes only the two synthetic journals
after the verified kill. It must fail the exact restored-counter and restored-
settings assertions. A generic startup/container failure does not satisfy this
negative control. Its logs and failed result are retained separately.

## Execution And Evidence

Compile `PlayerDataCrashFixture` through the existing pinned
`amxx-weapon-selection.ps1` toolchain and package it with the gameplay fixtures.
The source manifest binds the new fixture, unchanged product and compiler entry
point. The existing engine runner verifies the manifest before execution.

```sh
python3 -B tools/smoke/test-player-data-crash.py --integration \
  --cache /operator/gameplay-assets --compiled /operator/gameplay-fixtures \
  --output /operator/player-data-crash
```

The runner uses the existing digest-pinned ReHLDS/GameDLL/AMXX/ReAPI inputs,
network isolation, no published ports, bounded resources and owned-container
cleanup. Source and compiled mounts are read only. The host-writable evidence
directory contains synthetic logs, checkpoint/journal bytes, the kill receipt
and separate positive/negative results. No live vaults or player identifiers
are read. The native scenario is explicitly invoked with `--player-data-crash`;
it exits nonzero on the currently observed loss. The integration wrapper stops
at this failed positive prerequisite, before its missing-journal negative run.

CI compiles/packages the fixture and validates the evidence checker and shell
syntax. It does not run the known-failing preservation scenario as a required
gate. A green ordinary CI therefore cannot imply crash durability. Once the
underlying persistence policy is fixed, enable the strict positive/negative
native chain and retain its diagnostics as a required acceptance gate.

Local native acceptance and hosted CI are separate gates. Publication and
independent downloaded-artifact verification remain pending until recorded.

## Local Finding And Next Decision

The clean baseline and both players' updated readbacks passed. The engine was
then killed with signal 9 / exit 137 and without `plugin_end`. Both `.vault`
files still matched the clean checkpoint (75 and 261 bytes); both journals were
zero bytes. On reopening with reversed slots, the strict restored-state check
reported seven failures spanning saved totals, the crossed rank threshold,
language/loadout and HUD preferences. The second successful-state reopen and
missing-journal negative control were not reached after that failed prerequisite.

The upstream source for the pinned AMXX 1.10.0.5481 tag resolves to
`eaab23f0068b34e295987486ed5ca89408326271`:

- [`Journal::Begin` / `Write_Insert`](https://github.com/alliedmodders/amxmodx/blob/eaab23f0068b34e295987486ed5ca89408326271/modules/nvault/Journal.cpp)
  open the journal as a buffered file and append through `BinaryWriter` without
  an explicit flush on each insert.
- [`BinaryWriter`](https://github.com/alliedmodders/amxmodx/blob/eaab23f0068b34e295987486ed5ca89408326271/modules/nvault/Binary.cpp)
  uses `fwrite`.
- [`NVault::GetValue` / `SetValue`](https://github.com/alliedmodders/amxmodx/blob/eaab23f0068b34e295987486ed5ca89408326271/modules/nvault/NVault.cpp)
  use the in-memory map for lookup/readback. A matching readback alone does not
  demonstrate that journal bytes have left the process.

These source observations explain the pinned-engine reproduction. They are not
evidence of a production crash or proof that every write is always lost. Do not
add a test-only close/flush, fill the buffer artificially or relax the expected
state to make preservation appear successful.

The next product decision must define the acceptable crash-loss window and a
bounded persistence/checkpoint policy, including write cost and compatibility.
Implement and verify that separately before enabling mandatory crash acceptance.
Changing production, nVault internals or the shipped addon is outside this slice.

## Claim Limits

The separately approved [implementation](player-data-durability-implementation.md)
now passes local process-crash preservation with the new native module. This
does not rewrite the original failure or establish production acceptance.

SIGKILL leaves the operating system, page cache and underlying storage running.
This does not prove survival of power loss, kernel/host failure, torn writes,
disk loss, arbitrary journal corruption or a crash at every write instruction.
No forced fsync, zero RPO, atomic multi-player transaction or new recovery
guarantee is claimed. The human gameplay pilot and seven-day Archive assessment
remain independent and unfinished.
