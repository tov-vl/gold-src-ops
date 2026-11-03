# v2.102 - Backup warnings through the existing owner channel

The existing private IRM/email route is extended to the five player-data backup
warnings, preserving agent notifications, warning holds, grouping and repeat
intervals. Exact component/name matching and sanitized, family-specific payloads
keep unrelated alerts and private diagnostic fields outside the channel.

No addon, game behavior, backup record, schedule, credential or provider
integration changes. An already firing historical warning is expected to notify
when the route is enabled; its original evidence remains unchanged.

Delivery and acceptance are in progress. See [scope and gates](v2.102-backup-notifications.md).
