# v2.98 - Owner notification delivery

Local implementation adds a private, digest-pinned Alertmanager and a bounded
route for the five existing agent warnings. The selected destination is the
owner's existing Grafana Cloud IRM/email. Payloads omit private topology, queries
and arbitrary labels/annotations; source resolution is enabled.

Provider configuration and protected webhook transport were explicitly approved.
The first isolated canary confirmed owner email and source resolution; it exposed
a scalar-formatting defect corrected before production activation. A new
immutable candidate and production acceptance remain pending. No separate
recovery email is claimed. Old availability workers and history remain untouched.
