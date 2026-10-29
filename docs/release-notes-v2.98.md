# v2.98 - Owner notification delivery

Local implementation adds a private, digest-pinned Alertmanager and a bounded
route for the five existing agent warnings. The selected destination is the
owner's existing Grafana Cloud IRM/email. Payloads omit private topology, queries
and arbitrary labels/annotations; source resolution is enabled.

New provider configuration and protected webhook transport still need their
specific decision. CI, publication, production rollout and email acceptance are
pending. No deployment or separate recovery email is claimed. Old availability
workers and delivery history remain untouched.
