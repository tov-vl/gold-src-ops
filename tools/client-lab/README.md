# Client lab tooling

Repository-owned source for a bounded Windows Counter-Strike 1.6 acceptance
runner. The controller prepares an isolated local HLDS fixture and requests work
from a second laptop through a fixed SSH command handler. No cloud VM is needed.

See [the runbook](../../docs/client-lab.md) before preparing a local profile.
The example profile is synthetic and cannot be used as an installed activation.
Generated bundles, keys, machine configuration, game assets and evidence stay
outside tracked source. This directory does not provision Windows access.

Entry points:

| File | Purpose |
| --- | --- |
| `New-LabBundle.ps1` | Validate a private profile and render a fresh nine-file runner and controller bundle |
| `New-LabRun.ps1` | Prepare a fresh RU or EN fixture from hash-pinned local assets and matching activation |
| `New-GunsBaselines.ps1` | Bind eight reviewed native weapon-menu baseline frames for signed adoption |
| `Invoke-LabCampaign.ps1` | Prepare and manage a six-case matrix with Steam hold, one-attempt dispatch, same-ID reconciliation and separate review |
| `Build-FixtureHelper.ps1` | Rebuild and hash-check the fixture-only helper with the pinned offline AMXX toolchain |
| `lab_fixture.py` | Explicit plan/start/relay/stop lifecycle, with no client dispatch |
| `New-ArchivePlan.ps1` | Select exact completed requests for signed queue maintenance without deleting evidence |
| `New-UpdaterRecovery.ps1` | Prepare a predecessor-bound local trust-engine migration for receipt-bound inbox retirement |
| `Test-ClientLab.ps1` | Offline policy, parser, native compilation and synthetic-frame tests on Windows |
| `fixture_vgui_guard.sma` | Fixture-only join-menu suppression, synthetic leaderboard data and independent panel/movement markers |

Run policy checks from a fresh PowerShell 7 process on Windows:

```powershell
./tools/client-lab/Test-ClientLab.ps1 -OutputDirectory (Join-Path $env:TEMP ('client-lab-check-' + [guid]::NewGuid()))
python -B -m unittest discover -s tools/client-lab/tests -p test_*.py
```

The native harness targets .NET Framework 4.8 and C# 5 to preserve compatibility
with Windows PowerShell 5.1. Tests use mocked process/task/SSH operations and
synthetic pixels. They never activate a desktop window or send input. A matching
Windows Desktop runtime enables an additional PowerShell 7 compilation check;
its absence does not skip the mandatory target-runtime compilation.
Normal .NET dependency restore may use NuGet; no lab endpoint, Steam or Docker
runtime is contacted by the policy suite.

For the unified controller workflow and its explicit live gate, see
[campaign operations](../../docs/client-controller.md) and
[readiness](../../docs/client-controller-readiness.md).
The separate [weapon-menu scope](../../docs/client-guns-acceptance.md) defines
its two-case matrix, required baselines and nine retained frames per trial.

To check the fixture's active-menu observer without a client, compile its test
binary and use the existing isolated server harness:

```powershell
./tools/client-lab/Build-FixtureHelper.ps1 -CacheDirectory <compiler-cache> -OutputPath <new-test.amxx> -MenuTest
./tools/client-lab/Build-FixtureHelper.ps1 -CacheDirectory <compiler-cache> -OutputPath <new-ammo-test.amxx> -AmmoTest
python -B tools/client-lab/tests/run_fixture_menu.py --compiled <new-test.amxx> --ammo-compiled <new-ammo-test.amxx> --cache <engine-assets> --output <new-output>
```

The compiler cache is the pinned Windows AMXX/ReAPI toolchain. The engine asset
cache and both digest-pinned images must already exist; this test refuses missing
inputs instead of downloading them. It runs the actual Pawn observer with real
AMXX menu resources. Client state, network-message writes and diagnostic capture
are substituted in the menu test binary. The optional ammo test runs the included
observer with real inventory/ammo natives and carried weapon entities. Its fresh
synthetic player has a full inactive pistol, then an explicitly emptied magazine
as a negative control. No ammo or inventory change is allowed during either
observation; the product plugin is not loaded. The disposable server has no
network or published ports;
no Steam login, game client, second laptop or native input is used. The test
binary does not match the helper pin and cannot be used by `New-LabRun.ps1`.

Helper 1.6.2 uses direct ReAPI clip/reserve getters for the strict post-respawn
check and its eight-field `FIXTURE_GUNS_LOADOUT` report. One separate fixed
`FIXTURE_GUNS_CACHE` line retains both AMXX return codes and cached ammo values.
Weapon-presence terms and required AK-47 30/90 and Desert Eagle 7/35 counts are
unchanged. The read-only observer never repairs state or retries. The isolated
harness covers every single-term mismatch, stale-cache independence, exact
ordered reports and dead/non-respawn refusal. Both test switches are mutually
exclusive; their binaries cannot satisfy the ordinary helper pin.
