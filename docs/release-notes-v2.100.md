# v2.100 - Next-spawn weapons in `/profile`

Addon 0.39.0 adds the selected primary weapon and pistol to the RU/EN profile,
with the existing saved/session-only indicator. A matching validated in-memory
snapshot supports the saved claim; missing, pending or failed persistence does
not. Viewing the profile performs no storage access or equipment change.

Pinned compilation, dictionary/package boundaries and the complete 21-scenario
engine suite passed locally. All 162 language/pair/persistence combinations fit
the existing MOTD buffer, reaching at most 1177 of 1535 bytes. Real nVault
failure/reset and fresh-process reload checks passed.

Product CI, immutable package verification, guarded empty-server installation
and independent technical acceptance are pending. Real-client RU/EN acceptance
is held while Steam is occupied. Gameplay, storage formats and application
services are preserved. See [scope and gates](v2.100-profile-loadout.md).
