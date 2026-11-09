# GoldSrcOps v2.116.0

Adds owner notifications for the four recorded operational credential deadlines
through the existing private Prometheus, Alertmanager and Grafana IRM email path.
Warnings distinguish 30-day, seven-day, reached-deadline and unknown metadata
states, with a separate unavailable-observation warning. They do not establish
live credential validity and preserve conservative minute precision.

Delivery retains existing grouping/repeat behavior and sanitized fixed-purpose
labels. No secret value, provider key ID or exact provider date is exported.
Keys, permissions, provider configuration, archive scheduling, game and backups
are unchanged. See [scope and acceptance](v2.116-credential-notifications.md).

Release acceptance and publication are pending.
