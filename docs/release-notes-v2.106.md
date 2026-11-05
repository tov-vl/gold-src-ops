# v2.106 - Independent monitoring heartbeat

## Prepared change

- Add a fixed always-firing heartbeat rule and an exact private Alertmanager
  route to Grafana Cloud IRM's heartbeat endpoint.
- Keep the existing 17 warning tuples, owner-email projections and histories.
- Send only three constant identifiers. Keep the additional URL in a protected
  read-only secret, disable redirects and omit resolved heartbeat pings.
- Include a pinned real-process rehearsal for source/sender loss, expiry,
  recovery, routing, privacy and delivery failures.

The heartbeat observes the Prometheus/Alertmanager/outbound/IRM path. Other
services and the email provider are not independently verified by its presence.
The five-minute receiver timeout is not an end-to-end outage-detection SLA;
retained active alerts can extend detection when only Prometheus fails.

## Delivery status

Implementation and local verification are separate from the pending provider
decision, protected endpoint transport, signed publication and target rollout.
No production change, missed-heartbeat email or actual IRM recovery is claimed
yet. Only Alertmanager will need a reviewed recreation for the secret mount.
Game, addon, agent, data, application images and public access remain outside
the scope. See [scope and gates](v2.106-monitoring-watchdog.md).
