# v2.77 - Player-search retry countdown

Accepted on 2026-10-15. Stable [v2.77.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.77.0)
reuses the verified candidate digests without rebuilding.

- A busy player search shows a localized RU/EN countdown using the delay returned
  by the server. The submit button and Enter wait until that deadline.
- The nickname remains editable and is preserved. At expiry, the player can
  manually retry; no automatic search is made.
- Ranking refresh preserves the countdown. Returning to the page accounts for
  elapsed time, and assistive technology receives one completion announcement.
- The existing form remains usable without JavaScript. API/addon cooldown, cache,
  data and monitoring policy are unchanged.

This R1 release updated only Web. RU/EN countdown, early Enter blocking, ranking
refresh, manual retry and no-JavaScript search passed in production. The new Web
instance recorded six successful searches and two busy responses, matching the
eight browser submissions. No search failure series or active Web warning was
observed. Eight other containers, mounts and monitoring configuration were
preserved. Game-client visual checks, two-player play and long-window SLO claims
remain independent. See the
[readiness record](v2.77-readiness.md) and
[product contract and tests](player-search-countdown.md).
