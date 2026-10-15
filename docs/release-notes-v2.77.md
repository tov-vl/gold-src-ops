# v2.77 - Player-search retry countdown

Prepared release notes. Publication and production acceptance are pending.

- A busy player search shows a localized RU/EN countdown using the delay returned
  by the server. The submit button and Enter wait until that deadline.
- The nickname remains editable and is preserved. At expiry, the player can
  manually retry; no automatic search is made.
- Ranking refresh preserves the countdown. Returning to the page accounts for
  elapsed time, and assistive technology receives one completion announcement.
- The existing form remains usable without JavaScript. API/addon cooldown, cache,
  data and monitoring policy are unchanged.

This is an R1 Web-only release. Its repository checks passed; deployed behavior
and stable artifact identity remain separate gates. See the
[readiness record](v2.77-readiness.md) and
[product contract and tests](player-search-countdown.md).
