# Fresh manual player-data copy

## Target acceptance - 2026-11-03

After the separately accepted uncertain-slot recovery, one new manual copy was
captured with the existing installed guards. Independent external A2S/RCON and
the final local occupancy checks confirmed zero players and bots before stop.
Only the game was stopped and started once; the agent kept its process and
InvocationID. The measured stop/start/postflight interval was 24.237 seconds,
within the reviewed 120-second budget.

The closed bundle contains both required vault files. Its manifest records
the journals as absent after clean shutdown; this differs from the two empty
journals present during the live preflight. Closed inventory and bundle bytes
were checked before starting the game. The same
runtime was running and independently verified before any transfer or off-host
operation. No addon, executable, profile, permission, timer or provider setting
changed, and no Steam/client launch was needed.

The bundle was transferred through existing operator SSH channels, held only
in laptop memory, and retained in protected target directories. Its identity
matched on both hosts. One encrypted Archive and one separate Recover of the
same exact snapshot passed: SHA-256, manifest, file inventory, extraction and
readback bytes matched. The Archive took 24.65 seconds; Recover took 13.36
seconds. Neither operation restored files into the working game directory or
ran retention, pruning, initialization or an automatic retry.

The verified existing Archive/Recover records were registered once in the
compatible manual-copy registry. Capture time remains 2026-11-03T09:02:10 UTC
(12:02:10 MSK). The old weekly last-success and every retained uncertain-slot
record stayed byte-identical. Registration did not claim a successful weekly
cycle or reset its failed service.

## Independent evidence and claim limits

Acceptance at 09:10:23 UTC confirmed current public game availability, the
unchanged agent, accepted game baseline and all ten control-plane container
identities. Both agent loops were fresh/idle. Ordinary Prometheus observation
was current and valid, with `copy_source=2` (manual), `fresh=1`,
`requires_reconciliation=0` and the actual new capture timestamp. Grafana's
provisioned manual-source mapping was confirmed.

`PlayerBackupCopyUnavailable` resolved naturally. The historical weekly
service failure remains visible as `service_failed=1` and
`PlayerBackupInterventionRequired`; no alert, service or IRM group was reset.
The next real weekly attempt still follows the existing calendar.

Five focused offline orchestration tests passed: one stop/capture/start,
occupancy refusal, known-stopped restart after capture failure, uncertain stop
without repetition and start timeout without a second start. No product source
changed, so the complete local .NET gate and InspectCode were not rerun. Target
acceptance and documentation CI remain distinct evidence.

This accepts a closed capture and encrypted off-host byte/readback integrity.
An isolated AMXX engine recovery of this actual copy remains a separate proof;
synthetic engine tests are not presented as that proof. The current accepted
addon remains 0.38.0. Backup freshness is restored; the prepared 0.39.0 addon
still needs its own reviewed installation and independent technical/client
acceptance. Raw archives, snapshot identifiers, player values and private host
details remain outside Git.
