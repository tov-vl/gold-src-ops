# v2.103 - Resilient agent health publication

Health snapshots use unique sibling temporary files so an interrupted attempt's
leftover cannot block later diagnostics. Complete serialization precedes
replacement; owned-attempt cleanup preserves older orphan evidence. Existing
reader schema, freshness and queue/delivery behavior are retained.

The agent-only installer also recognizes the exact already installed pending
gate for subsequent upgrades, rejects altered/duplicate gates and skips writes
to unchanged references during apply/rollback.

Released on November 4 as
[game-event-agent-v2.103.0](https://github.com/tov-vl/gold-src-ops/releases/tag/game-event-agent-v2.103.0).
Local Quality Gate, scoped InspectCode, complete PR/main CI, real Linux package
fixture and independent agent-only production acceptance passed. Stable reuses
the accepted candidate bytes. Results and limits are recorded in
[scope and gates](v2.103-agent-health-publication.md).
No game restart, addon update, queue migration or new credential is included.
