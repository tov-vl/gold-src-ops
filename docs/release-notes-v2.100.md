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
the verified product CI binary without rebuilding. On November 3, one guarded
installation returned to 0.38.0 after the operator probe rejected a public
projection that still reported offline immediately after restart. The coordinator
exited and missed rollback acceptance. A separately authorized reconciliation
retained the original journal and proof, closed the outcome as rolled-back and
cleared only its matching addon pending without another restart or install.
Independent checks confirmed healthy 0.38.0, the original baseline, preserved
agent/queue/control-plane and passing backup guards. The candidate runtime was
not accepted; this probe failure does not establish a candidate defect.

The corrected operator probe separates direct runtime checks from bounded public
convergence; the coordinator continues servicing rollback after a rejected probe
without resending an uncertain response. Nineteen isolated failure/guard cases,
fresh preflight, installed dry run and predecessor callback passed. A distinct
guarded operation reached accepted at 10:53:29 UTC on November 3. Independent
checks confirmed exact 0.39.0, healthy markers, preserved closed inventory,
unchanged agent/queue/control-plane, the derived three-entry baseline change,
no pending and passing backup guards. Public state converged separately.
The package was not rebuilt and the installed workflow was not replaced.

Seven completed client requests were archived with all raw results and image
references preserved, unchanged runner code and zero game processes. Real-client
RU/EN acceptance resumed after the owner confirmed Steam free. Fresh profile
captures were visually reviewed for both languages, showing the full default
MP5 + USP and localized connection-only wording without clipping. Fixture pins
now require 0.39.0; only the two profile baseline hashes change. Signed adoption
and fresh OK/alias/returned-controls checks remain pending. Gameplay, storage
formats and application services are preserved. Technical delivery is accepted.
The original failed operation must not be replayed.
The complete release remains open; see
[scope and gates](v2.100-profile-loadout.md).
