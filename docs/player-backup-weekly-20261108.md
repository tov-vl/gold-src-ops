# Weekly player-backup acceptance - November 8, 2026

## Scheduled copy and read-only observation

The ordinary weekly cycle on 2026-11-08 started at 04:00:00.659 MSK, captured
the closed copy at 04:00:15.699 MSK and completed at 04:00:59.555 MSK. Its
58.896-second result was verified, with encrypted Archive/readback registration.
The retained bundle hashes matched across the hosts and the Archive readback;
both retained manifests and their fixed inventories were validated. The copy
contains two vaults and no journals. Last-success matched the completed cycle.

Read-only observation agreed with the direct backup monitor, Prometheus and
the existing Grafana backup views: the copy was fresh, its source was the
automatic weekly cycle and no backup alert was active. The timer remained
enabled with its next scheduled activation on November 15 at 04:00 MSK. These
are dated observations, not a promise about future monitoring or timer state.

The 20 historical immutable ledger records matched the earlier reference;
last-success advanced through the ordinary weekly cycle. All 23 current ledger
JSON records stayed unchanged during observation. The November 1 slot remained
closed-uncertain. The observation did not launch another backup, Archive or
Recover, change service state, reset a failure or resolve an alert manually.

## Separate engine acceptance

The retained verified Archive readback passed a separate isolated engine
exercise on the same date. No new off-host Recover was performed. The accepted
[v2.101 fixture and method](v2.101-player-data-engine-recovery.md) were reused:
exact addon 0.39.0 source/dictionary and compiled fixture bytes, AMXX 1.10.0.5481,
ReAPI 5.24.0.300, ReHLDS 3.15.0.896, ReGameDLL 5.30.0.814 and the existing
digest-pinned local image. Cached public runtime archives were hash-verified
before extraction. The fixture binary was reused without recompilation.

A synthetic positive control passed two starts covering all five record
families. A separate deliberately mismatched expectation produced a nonzero
exit and explicit failed assertions, proving that mismatches reject acceptance.

The actual copy then passed two independent engine process starts. Each
verified every native value and timestamp plus the matching product loading
or derived state:

| Record family | Records per start | Product evidence |
| --- | --- | --- |
| Statistics | 6 | Saved kills/deaths and derived rank matched |
| Preferences | 12 | Language, primary/pistol and saved confirmation matched |
| HUD | 1 | Stored toggle and saved status matched |
| Streak | 5 | Stored best matched; current-life progress remained separate |
| Names | 19 | Stored name projection matched |
| Total | 43 | Records, not a count of distinct players |

Both starts reported zero failed assertions, zero attempted product writes and
no AMXX error files or fatal/runtime markers. The fixture substitutes synthetic
client identity and observes/rejects product writes; native nVault reads and
actual product loaders remain real. This does not test Steam authentication.

The container had no network or published ports, two CPUs, 1 GiB RAM, 256 PIDs
and a 180-second outer deadline. Protected input passed through existing SSH
into process memory and container tmpfs. Vaults, expected records and native
logs stayed there; only aggregate receipts and hashes were retained. No private
player identities, values, bundle identities or host topology are published.

The actual-copy container exercise took 37.375 seconds, including preparation
inside the container, two starts and removal. This excludes SSH transfer,
local runtime preparation, off-host recovery and full-server reconstruction.
It is not a production RTO measurement.

## Preservation and claim limits

Independent postflight matched source bundle/vault and Archive-operation
hashes, all 23 backup ledger records and game/agent process identities. Owned
containers were absent and pre-existing container identities preserved.
Task-created public runtime inputs and synthetic vault files were removed;
aggregate receipts and frozen input identities remain in the operator record.

Original Archive metadata retains engineAcceptance=pending. The engine result
is a separate acceptance bound to the exact retained copy; it does not rewrite
the original operation or the November 1 uncertain history. This follows the
[player-data recovery boundary](game-player-data-backup.md#изолированное-восстановление).

No production restore, restart, addon installation, Steam session, credential
or permission change was performed. The result proves this closed retained
copy readable under the pinned runtime and current product loaders. It does
not prove full-host recovery, post-snapshot writes, arbitrary schema
compatibility, crash-journal replay, fsync durability or RPO/RTO. This copy has
no journal operations, so their replay was not exercised.

This repository follow-up is documentation only. The complete .NET gate and
InspectCode are not applicable to unchanged application code; the publication
uses the repository documentation validator and required documentation CI.
