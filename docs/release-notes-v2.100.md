# v2.100 - Next-spawn weapons in `/profile`

Addon 0.39.0 adds the selected primary weapon and pistol to the RU/EN profile,
with the existing saved/session-only indicator. A matching validated in-memory
snapshot supports the saved claim; missing, pending or failed persistence does
not. Viewing the profile performs no storage access or equipment change.

Pinned compilation, dictionary/package boundaries and the complete 21-scenario
engine suite passed locally. All 162 language/pair/persistence combinations fit
the existing MOTD buffer, reaching at most 1177 of 1535 bytes. Real nVault
failure/reset and fresh-process reload checks passed.

Product PR #457 merged after all six required checks passed; complete required
post-merge CI passed too. Published compiler
and engine artifacts were independently verified; the immutable package uses
the verified product CI binary without rebuilding. Guarded installation and
independent technical acceptance remain pending: SSH is unavailable and the
public projection reports the game host offline. No production changes occurred.

Seven completed client requests were archived with all raw results and image
references preserved, unchanged runner code and zero game processes. Real-client
RU/EN acceptance remains held while Steam is occupied. Gameplay, storage formats
and application services are preserved. The release is not complete; see
[scope and gates](v2.100-profile-loadout.md).
