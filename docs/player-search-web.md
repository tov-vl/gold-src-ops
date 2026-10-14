# Player search Web foundation

This implements step 3 of [the search plan](player-search-feasibility.md), using
[the API contract](player-search-api.md). It changes Web and tests only; the
combined addon/API/Web feature still requires its R2 release and acceptance.
No database migration, production setting, game menu or MOTD changes are included.

## Page behavior

`/leaderboard` gains an inline RU/EN search when the Web configuration
`PublicPlayerSearch:Enabled` is `true`. The default is `false`. The API has its own
independent flag; enabling Web does not enable API or addon lookup.

The form submits a normal static SSR POST with antiforgery protection and works
without JavaScript. Search requires the exact last saved nickname, including
case; surrounding ASCII spaces are removed. It does not search by substring,
SteamID or global ranking position. Validation retains the API's 1-31 UTF-8 byte
limit and rejects controls, invalid Unicode and identifier-like input.

Results contain at most ten cards with escaped names, rank, kills, deaths and
progress derived from the same row. Identical rows remain separate. Explicit
copy explains non-unique nicknames, truncation and no matches. Capture time is
the lookup snapshot time, not the player's last match or last durable save.
Empty, invalid, busy and unavailable states replace prior results. Busy includes
a bounded manual retry delay. The [countdown enhancement](player-search-countdown.md)
uses that delay to disable early submission with JavaScript and announce when
manual retry is available; without JavaScript the existing form stays usable.
Search never runs on typing, automatically retries or polls. The existing top-ten
refresh replaces only its own panel and does not repeat or overwrite search,
including after an automatic refresh.

## Boundaries

- Browser posts to Web; Web alone calls the fixed API POST route. No nickname
  enters a generated URL, redirect, link, referrer, metric tag or application log.
  No search data is written to browser storage. The browser's normal antiforgery
  cookie remains necessary for POST.
- Enabled leaderboard responses use `Cache-Control: no-store` and
  `Referrer-Policy: no-referrer`. Disabled Web rejects leaderboard POST with 404.
- Before antiforgery and component processing, the Web boundary accepts only
  URL-encoded forms with absent or UTF-8 charset. Body reads have a three-second
  deadline and a 4096-byte cap, including bodies without Content-Length. Form
  parsing is limited to eight values, 64-character keys and 256-character values.
- The API client uses one five-second budget for headers, body and deserialization,
  links caller cancellation, disables redirects and never retries. It reads at
  most 4097 bytes to reject bodies over 4096 bytes.
- Web checks status/state agreement, capture age, exact requested name, ordinal,
  row count, order, counters, rank, retry delay and truncation before display.
  Missing API routes, invalid/cross-query responses, transport failures and
  timeouts show unavailable without stale rows or raw exception text.
- API/addon remain responsible for lookup admission and cache limits. Web does
  not add a separate query cache or durable store.
- Existing duration metrics add the fixed operation `player_search`. Outcomes
  are `success`, `busy`, `not_found`, `error`, `timeout` and `canceled`; no query
  labels are recorded. Existing Grafana per-operation panels can display these
  series. Search warning coverage was installed and independently accepted in
  the separate [monitoring extension](player-search-alerts-readiness.md); its
  production installation is independent of this Web slice. Empty matches are `success`;
  `not_found` means an unavailable API route, not absence of a matching player.

## Verification and remaining release gate

Focused checks cover SSR escaping, duplicates, RU/EN, invalid inputs, disabled
configuration, CSRF, content type/charset, oversized fixed/chunked forms, bounded
API responses, malformed/cross-query rows, cancellation and body deadlines.
Browser checks cover desktop/mobile at 1440/390/320 pixels, keyboard submission,
truncation, rank progress, no browser API requests or URL query leakage, manual
and automatic top-ten refresh, and RU/EN operation without JavaScript. Screenshots
are local synthetic fixtures, not production evidence.

Local verification completed on 2026-10-14:

- 59 focused search/leaderboard checks passed with browser opt-in enabled.
- The complete Quality Gate passed: restore with audit enforcement, format,
  build with zero warnings/errors, 1146 tests and package vulnerability scan.
  The ordinary suite skipped 59 opt-in browser tests; the relevant browser
  scenarios were exercised in the focused run above.
- Documentation validation checked 198 Markdown files and 1050 local links.
- RU/EN desktop/mobile screenshots were inspected after the contrast correction.

Required PR/main CI, immutable addon/API/Web publication,
empty-server addon rollout, affected-container deployment and independent public
acceptance remain separate gates. Feature flags stay off until the matching
artifacts and rollback boundary are reviewed. A missing/disabled API remains a
safe unavailable state. Game-client visual acceptance and two-player testing
remain outside this slice.
