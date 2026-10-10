# GoldSrcOps v2.59 - Leaderboard Page Refresh

Candidate `v2.59.0-rc.1` and stable `v2.59.0` pending publication/acceptance.

- Public leaderboard refreshes in a visible tab once a minute without replacing
  the whole document; manual refresh preserves focus and join navigation.
- Age continues between reads; errors mark previous results stale, and
  unavailable/expired data do not retain old rows as fresh.
- Hidden tabs stop reads; no overlapping requests, immediate retries, browser
  API credentials or persistent browser state. SSR still works without JS.
- Web-only runtime scope. API, addon, gameplay, saves, scoring, schema,
  monitoring and weekly backup schedule are unchanged.

See [readiness](v2.59-readiness.md) and [contract](v2.59-leaderboard-refresh.md).
