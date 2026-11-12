# GoldSrcOps v2.121

Add bounded A2S worker-cycle telemetry and a private Grafana view. Distinguish
Unknown, Running, Error, Disabled and Stale without interpreting failed server
responses as a dead worker. Three held warnings reuse the existing private
IRM/email channel and safe fixed payload. The operational budget does not change
query deadlines, retries, cancellation or game-server incidents.

R3 monitoring-policy rollout: API plus Prometheus/Alertmanager/Grafana files.
No migration, game/agent change, new credential or Archive schedule change.
Release and target acceptance are pending.
