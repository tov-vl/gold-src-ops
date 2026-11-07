# v2.111 release notes

The private Operator game-host health page now lists existing Pending/Firing
warnings with fixed descriptions and Grafana detail links. Fresh inactive
rules produce an explicit empty result; missing, invalid or stale rule data
produce a separate unavailable/stale state. Other health measurements remain
independent.

The additive response and static SSR page retain Operator authorization and
server-held BFF tokens. No warning threshold, notification, game addon, agent,
backup journal or database change is included.

See [scope, compatibility and readiness](v2.111-operator-health-warnings.md).
Candidate publication, production acceptance and stable promotion are pending.
