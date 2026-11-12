# GoldSrcOps v2.121

Add bounded A2S worker-cycle telemetry and a private Grafana view. Distinguish
Unknown, Running, Error, Disabled and Stale without interpreting failed server
responses as a dead worker. Three held warnings reuse the existing private
IRM/email channel and safe fixed payload. The operational budget does not change
query deadlines, retries, cancellation or game-server incidents.

R3 monitoring-policy rollout: API plus Prometheus/Alertmanager/Grafana files.
No migration, game/agent change, new credential or Archive schedule change.
Released and installed: [v2.121.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.121.0).

Required CI, independent publication/runtime/policy/provisioning and isolated
IRM firing/resolved acceptance passed. Only API was recreated; fourteen other
captured containers were retained. Stable promotion reused all candidate digests
without rebuild. Inbox receipt remains separate from provider delivery proof.
