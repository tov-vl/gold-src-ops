# v2.101 - Verified player-data engine recovery

The previously accepted November 3 manual copy now has a separate isolated
AMXX engine proof. Two fresh engine processes each read all 42 recorded values
and timestamps, then checked product statistics/ranks, language and weapon
preferences, HUD, saved streaks and stored names. Both passed without attempted
product writes, AMXX errors or runtime failures.

Synthetic positive and deliberately mismatched controls verified acceptance
and failure propagation before actual-data execution. An initial local intent
collision was reconciled as not launched; a distinct execution then passed.
The accepted 0.39.0 source, pinned toolchain and networkless container boundary
were retained. Private data stayed in memory/tmpfs, the container was removed,
and independent checks confirmed unchanged original source hashes and existing
local container identities.

This is an operational acceptance milestone with documentation-only publication.
No production restore, new backup, addon/service deployment, credential change
or Steam session occurred. Addon 0.39.0 remains accepted. This proof covers this
closed copy and its actual records; full-host recovery, later writes,
crash-journal replay and production RPO/RTO remain unproved.

See [scope, results and limits](v2.101-player-data-engine-recovery.md).
