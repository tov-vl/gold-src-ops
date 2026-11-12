# GoldSrcOps v2.125

The Operator operations overview adds incident outbox and receiver queue cards,
including pending/processing/dead-letter counts, oldest pending time, receiver
mode and worker configuration. Configured sources are read independently;
Unknown/Stale and disabled access never become an empty healthy queue.

Catch-up / sandbox mode is separate from the provider worker. These incident
queues do not establish email receipt and are separate from Prometheus to
IRM/email. Fixed detail links and Refresh work without JavaScript. The existing
three-source report remains a separate download.

R1 Web-only delivery with exact prior Web rollback. API/receiver, monitoring,
workers, permissions, providers, game/agent and Archive cadence remain unchanged.
Released and installed:
[v2.125.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.125.0).
Required CI, artifact checks and independent runtime/authenticated browser
acceptance passed. Fourteen other containers were preserved; stable reused
candidate digests with all three build steps skipped. See [scope and evidence](v2.125-incident-delivery-health.md).
