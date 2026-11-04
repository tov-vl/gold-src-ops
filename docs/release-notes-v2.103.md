# v2.103 - Resilient agent health publication

Health snapshots use unique sibling temporary files so an interrupted attempt's
leftover cannot block later diagnostics. Complete serialization precedes
replacement; owned-attempt cleanup preserves older orphan evidence. Existing
reader schema, freshness and queue/delivery behavior are retained.

The agent-only installer also recognizes the exact already installed pending
gate for subsequent upgrades, rejects altered/duplicate gates and skips writes
to unchanged references during apply/rollback.

Local tests passed; scoped inspection, repository CI, immutable publication and
target acceptance are tracked in [scope and gates](v2.103-agent-health-publication.md).
No game restart, addon update, queue migration or new credential is included.
