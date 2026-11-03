# v2.102 - Backup warnings through the existing owner channel

The existing private IRM/email route is extended to the five player-data backup
warnings, preserving agent notifications, warning holds, grouping and repeat
intervals. Exact component/name matching and sanitized, family-specific payloads
keep unrelated alerts and private diagnostic fields outside the channel.

No addon, game behavior, backup record, schedule, credential or provider
integration changes. An already firing historical warning is expected to notify
when the route is enabled; its original evidence remains unchanged.

PR #467, full required/main CI, signed candidate publication and R3 technical
acceptance passed. Ten existing containers, routing-file inodes, backup rules,
dashboards, worker flags and credential metadata were preserved. A separate
canary's firing/resolved webhooks succeeded and the canary was stopped.
The owner confirmed the fresh firing email and the matching group's `Resolved`
state in IRM; a separate recovery email is not claimed. Signed stable v2.102.0
reuses the accepted candidate source and byte-identical ZIP/checksum without
rebuilding. Global latest remains application v2.94.0.
See [scope and gates](v2.102-backup-notifications.md).
