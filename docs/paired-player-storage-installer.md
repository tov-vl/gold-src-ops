# Paired Player Storage Installer

The [R3 plan](player-data-durability-rollout-plan.md) owns the rollout boundary.
The first candidate was rolled back after the persistent guard refused a module
whose SHA-256 differed from the stock pin in the pilot overlay. The second
candidate pairs storage with the guard and its persistent marker in one plan.
[Stable 0.40.0](player-storage-0.40.0-release.md) records production acceptance
and same-byte promotion of rc.2.

## Package And Frozen Plan

`tools/release/build-player-storage-pair.py` assembles existing verified CI
bytes. It does not compile the addon or replace a published manifest. It checks
the native and fixture source identities against the checkout, retains native
source/licenses, and selects the fixture's exact compiled addon. Its output has
purpose `paired-player-storage`, schema 1, addon 0.40.0 and process contract 1.

The five payloads are the weapon addon, production nVault module and the three
unchanged configuration/loader/dictionary files. Proof manifests bind both
binaries and the corresponding native source ZIP. The predecessor binds the
old standalone addon manifest and stock module hash. Wrong payload paths,
duplicate JSON fields, unknown schema, testing/fault-hook modules, non-i386
binaries, changed source/payload hashes and unsafe links/permissions are rejected.

The existing standalone installer stays blocked for 0.40.0. The new paired
installation receipt is schema 3, including the module hash and native contract.
Do not force it through the old schema-2 baseline refresher or standalone updater.
The independent release checker accepts schema 3 only when its five payload
hashes match the frozen baseline and its process contract is 1; existing schema-2
acceptance remains supported.

The operation plan has schema 2 and these fields:

| Field | Required meaning |
| --- | --- |
| `operationId` | Bounded neutral operation name |
| `previous`, `candidate` | Absolute staged package directory and exact manifest SHA-256 |
| `previousModule` | Absolute retained stock-module path and SHA-256 |
| `baselines.previous`, `baselines.candidate` | Absolute frozen files and SHA-256 for the exact old/new baseline bytes |
| `hooks.probe`, `hooks.backup`, `hooks.runtime` | Absolute reviewed root-script paths and SHA-256 |
| `persistent.previousGuard`, `persistent.previousMarker`, `persistent.previousBackupGuard` | Absolute retained startup guard, marker and installed backup guard library from the preflight, with SHA-256 |
| `persistent.candidateGuard` | Absolute staged candidate guard and SHA-256. The bytes must equal the reviewed persistent workflow shipped beside the coordinator. |

The library `ops/gameserver/player_storage_pair.py` computes the baseline from
the reviewed old rows and frozen package, receipt, guard and marker bytes. Exactly
the module, plugin, receipt, guard and persistent marker rows can change.
Identical repeated rows and their ordering are preserved. Conflicting duplicates
are rejected. Every unrelated row must match the host before replacement,
acceptance and rollback.

The coordinator derives the candidate marker by changing `guard_sha256` and
adding `storage_module_sha256` and `storage_plugin_sha256`. It verifies that
the current pilot overlay has exactly one standard nVault entry pinned to the
frozen stock binary. The overlay manifest and its hash chain stay intact.
The installed game guard uses the new marker only for the exact nVault entry,
checks the addon hash as well, and retains the overlay checks for all other
entries. A marker with no storage pins retains the stock behavior.
The coordinator installs the same guard library in the existing backup tool
directory and validates its exact bytes as part of the pair. This keeps the
scheduled backup and independent acceptance readers compatible with the
selected marker. The old library bytes are retained for rollback.

Stage tools/packages/hooks root-owned without group/other write access. The
Linux root entry point is `ops/gameserver/paired-addon-upgrade.py`, with required
`--plan` and `--plan-sha256`. Without `--apply` it verifies the frozen plan and
predecessor under the ordinary locks, without claiming an installation. This
mode can create lock files; it is distinct from the earlier direct read-only
host inventory. Execution requires explicit `--apply` and the concrete R3 decision.

## Coordinator And Data Boundary

The coordinator takes the existing schedule, capture and transition locks in
that order. It uses the existing addon pending marker so scheduled capture and
other upgrades cannot observe a half-installed pair. One immutable plan claim
prevents repeating the same operation, including after terminal refusal.

Before the first stop it verifies the predecessor, consumers, baseline,
runtime and storage probe, then immediately rechecks empty A2S. While the game
is closed it creates the existing four-file bundle and requires verified
encrypted readback and independent old/new-reader semantics through the frozen
backup hook. It invokes the real baseline/quiescence shell guard under the
already-held locks; it does not reacquire them through the standalone capture
command. A target backup gate cannot be replaced by a live inventory hash.

Both binaries, receipt, both guard copies, baseline and persistent marker have separate
compare-before-replace operations with fsynced intent and exact metadata.
While those writes are in progress, the coordinator checks quiescence, vault
bytes and the recognized static set. The startup guard refuses a partial
transition. The coordinator writes the candidate marker last, then runs the
full quiescent guard before the candidate start. It restores components in
reverse order and runs the full guard again before a predecessor start.
Closed player bytes must remain unchanged throughout installation. Unexplained
changes retain pending and require reconciliation.

Runtime acceptance requires the actual engine process from its service cgroup,
the verified mapped module inode/device, no restart and a frozen read-only
version/storage probe. It also evaluates the installed backup preflight.
The service MainPID may be a wrapper and is insufficient.
A second empty-server stop/capture/reopen compares private semantic digests
of sorted records and timestamps. This permits checkpoint ordering changes
without claiming equality from record counts or rewriting player data.

Known completed failures can restore exact predecessor binaries, receipt and
baseline with their original static metadata. Latest valid player data stays
in place and is checked with both readers before a post-start rollback. A new
player blocks any further stop. Unknown file state, interrupted commands,
unclassified hook failure or changed closed semantics require reconciliation.
No operation retry, player-data restore, pending reset or recovery-file deletion
is performed automatically. Pending is cleared only when its identity matches.

## Frozen Hook Protocol

Each reviewed synchronous hook receives `phase`, `evidence-directory`,
`plan-sha256` and `package-manifest-sha256` as separate arguments. It must emit
one bounded JSON response with schema 1, matching phase/plan identity and state
`verified`, `refused` or `uncertain`. A classified refusal exits successfully
with `state=refused`; a nonzero exit, invalid/unclassified response or timeout
is uncertain. Responses have closed field sets and must contain no credentials,
player identities, raw vault records or provider responses.
Each hook must also verify the hashes of any executable or secondary input it
uses, including the staged probe/readers. An outer script hash does not freeze
an unchecked dependency at another path.

- Probe success adds `samples=10000`, integer `p99Us <= 2000` and diagnostic
  `maxUs`. Maximum latency is not a separate SLA.
- Backup success binds `plaintextSha256`, `restoredSha256`, `semanticSha256`,
  `oldReaderSemanticSha256`, `newReaderSemanticSha256`, plus true `encrypted`,
  `offHost` and `independentRecovery` evidence. The hook must implement the
  existing approved owner transport and independent copy/reopen checks.
- Runtime success binds `manifestSha256`, `addonVersion`, `storageHealthy`
  and `processContract`, checking the selected 0.39.0/stock or 0.40.0/contract-1
  runtime without writing game state.

No permissive production hook or provider credential is shipped. The exact
environment-specific scripts, secret transport and timeout fit must be reviewed
and frozen in the execution bundle. Hook result fields are evidence bindings,
not independent proof that an arbitrary script performed the promised work.

## Native Probe And Local Rehearsal

`tools/native/build-player-storage-probe.py` builds separate Linux i386 tools
from verified upstream/module sources, without rebuilding the accepted module.
Its package retains hashes, builder identity and corresponding source/licenses.
The new tool probes only a newly created private synthetic directory under the
service identity: 10,000 commits, checked checkpoint, fresh-instance reopen and
synthetic orphan retention using `renameat2(RENAME_NOREPLACE)`. Cleanup removes
only its known created files. A failed probe retains its scratch directory.
Old/new tools can reopen explicitly named private restored copies; neither has
fault controls. Closed copies must be parsed and compared privately around
each reopen, not applied to live vaults.

`tools/smoke/paired-storage-rehearsal.py` freezes source and probe inputs, uses
digest-pinned Python and i386 runtime images and a SHA-256-pinned jq binary.
The fixture runs without network or ports. It retains per-phase receipts, measured probe
p99/maximum, source hashes and the test log. The root fixture exercises actual
file replacement, baseline/quiescence checks and the installed guard with a
stock-pinned pilot overlay and fast-reentry profile. It also checks encrypted
readback in a separate local directory with the actual old/new readers. Service
state, process identity and owner transport are fixtures. Provider availability,
engine startup and target-host acceptance are separate checks.

CI builds and retains the probe/source artifact and runs the frozen rehearsal
on the full path. Documentation/stable fast paths and the existing addon-only
path remain bounded. New paired tooling changes require full CI.

The first candidate's local acceptance passed 35 paired-workflow/contract cases with the real i386
readers/probe, actual backup guards and frozen source inputs. It includes partial
pair replacement, uncertainty and no replay, new-progress retention, metadata,
occupancy, conflicting/identical baseline repeats, mapped engine identity and
the existing weekly capture's pending refusal. All 14 backup-transport tests,
18 release-acceptance tests, 19 browser-coordinator helper tests and CI fast-path
checks passed separately. Python/shell/YAML syntax and documentation checks
are separate from hosted CI. No C# changed; the .NET suite and InspectCode were
not run. No remote publication, game restart or deployment was performed.

The first candidate's local assembly also matched the preserved installed receipt and
computed the exact 26-row baseline transition: three changed rows, unchanged
inventory order and identical repeats. This is a proposal, not an applied
production baseline. For the second candidate, the new guard and marker
account for two additional changed rows. The separately frozen backup guard
library is outside that baseline's inventory.

For rc.2, full PR/main CI, signed ZIP verification, the schema-2 execution
freeze and independent production acceptance passed. The published
[release evidence](player-storage-0.40.0-release.md) records both rollout
attempts and stable verification.
