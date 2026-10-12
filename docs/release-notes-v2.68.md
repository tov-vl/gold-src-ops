# v2.68 - Public status refresh

Release candidate preparation; not yet published or accepted.

The public dashboard refreshes every minute in a visible tab and on demand.
RU/EN, the selected 24-hour or 7-day history window, document, scroll and
connection controls are preserved. Without JavaScript, Refresh loads the page.

Observation ages advance between requests. Expired observations and failed
reads no longer confirm current availability. Map and player counts are hidden
when unconfirmed; previous fleet counts and historical A2S results remain
clearly labeled. A temporary connection-projection failure preserves the
last known connection command; removed configuration removes the controls.

Reads are anonymous, same-origin and bounded by timeout, size and cadence.
No API, database, authentication, game, addon or backup changes are included.
See [readiness](v2.68-readiness.md) and [contract](v2.68-public-status-refresh.md).
