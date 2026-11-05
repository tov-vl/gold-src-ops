# v2.106 - Independent monitoring heartbeat

## Delivered change

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

Accepted on 2026-11-05 after the owner's separate provider/endpoint decision.
PR #475 and full required/main CI passed. Signed candidate/stable publication
and independent downloads matched all five source entries; stable reused
accepted bytes without rebuilding.

Only Alertmanager was recreated, preserving its image and notification volume.
Prometheus was reloaded; the other nine containers, five dashboards, old rules,
warning histories and worker flags were preserved. Actual IRM missing-heartbeat
detection, provider email sending and automatic source-based recovery passed on
a separate test endpoint. The production heartbeat remains healthy.

Delayed cleanup let the short recovery sender's test timeout elapse again,
creating an extra test-only group/email. Test monitoring was disabled and that
extra group manually closed as cleanup, excluded from recovery acceptance.
Both histories remain intact. Owner inbox receipt and a separate recovery email
are not claimed. Game, addon 0.39.0, agent, data, application images/latest and
public access remain unchanged. See [scope and evidence](v2.106-monitoring-watchdog.md).
