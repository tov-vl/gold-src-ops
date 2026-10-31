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
| `Invoke-LabCampaign.ps1` | Prepare and manage a six-case matrix with Steam hold, one-attempt dispatch, same-ID reconciliation and separate review |
| `Build-FixtureHelper.ps1` | Rebuild and hash-check the fixture-only helper with the pinned offline AMXX toolchain |
| `lab_fixture.py` | Explicit plan/start/relay/stop lifecycle, with no client dispatch |
| `New-ArchivePlan.ps1` | Select exact completed requests for signed queue maintenance without deleting evidence |
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
