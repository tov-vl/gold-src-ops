# v2.98 - Owner notification delivery

Adds a private, digest-pinned Alertmanager and a bounded
route for the five existing agent warnings. The selected destination is the
owner's existing Grafana Cloud IRM/email. Payloads omit private topology, queries
and arbitrary labels/annotations; source resolution is enabled.

Provider configuration and protected webhook transport were explicitly approved.
One isolated canary confirmed owner email and source resolution; it exposed a
scalar-formatting defect corrected and tested before production activation.
Product PR #416 and correction PR #417 passed complete required and post-merge CI.

The [stable operations package](https://github.com/tov-vl/gold-src-ops/releases/tag/owner-notifications-v2.98.0)
was installed and independently accepted on 2026-10-29. Stable assets reuse the
accepted rc.2 bytes without rebuilding. The new route is active; nine existing
containers, four rule groups, three dashboards, observer and game/agent/queue
state were preserved. The first apply stopped on a false readiness probe and
cleaned up before changing Prometheus; native checks then passed. Global latest
remains application v2.94.0.

No separate recovery email, external host-outage coverage, exactly-once delivery
or SLA is claimed. Old availability workers and history remain untouched. See
[scope and acceptance evidence](v2.98-owner-notifications.md).
