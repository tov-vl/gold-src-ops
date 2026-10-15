# Player-search retry countdown

Merged through PR #352 on 2026-10-14 and accepted in production with the Web-only
v2.77 release on 2026-10-15. Stable promotion reuses the verified candidate.

## Boundary

R1, Web only. This enhances the existing static SSR form on `/leaderboard`.
The API response, server-wide cooldown, cache, feature flags, addon and data
formats are unchanged. No search is submitted automatically, no nickname is
stored in the browser or added to a URL, and no browser-to-API request is added.

- Only a validated `busy` response supplies a countdown of 1-10 seconds.
- With JavaScript, the submit button is disabled and the form rejects early
  submission, including Enter. The editable nickname is preserved.
- The timer uses an elapsed deadline rather than counting callbacks. Returning
  to a hidden/restored page updates the remaining time without starting again.
- At expiry the button becomes available and localized copy offers a manual
  retry. Focus is not moved; the keyboard can reach and activate the button.
  The visible timer is not a live announcement on every tick; a separate status
  announces completion once.
- Leaderboard refresh replaces only the ranking panel, leaving the countdown,
  form and name intact. A retry is still subject to the shared server limit;
  becoming locally ready does not reserve server capacity.
- Without JavaScript, or if the enhancement cannot initialize, the server-rendered
  button stays enabled and the existing localized busy message remains usable.
  Initial, successful, empty, invalid and unavailable states have no countdown.

## Verification

- Three focused browser checks passed: RU/EN countdown and manual keyboard retry,
  page lifecycle catch-up, and no-JavaScript compatibility. The timer test also
  verifies no extra lookup on early submit, countdown completion or ranking refresh,
  an edited nickname survives, and browser storage stays empty.
- All 67 selected search, leaderboard and language checks passed with browser
  opt-in enabled. One earlier run reported overflow in the existing long-name
  layout case; a focused run and the full selected rerun passed. Geometry details
  were added to that assertion to retain evidence if it recurs; no layout check
  was removed and no product workaround was applied without a reproduced cause.
- RU/EN screenshots at 320px were inspected; waiting text and the disabled button
  fit the layout. Other widths and long/duplicate names remain in the existing
  browser coverage.
- All 61 browser tests then passed separately, zero skipped, including the
  long-name layout case and unrelated pages sharing the App script list.
- Complete local Quality Gate passed: restore with audit, format verification,
  build with zero warnings/errors, 1146 ordinary tests and vulnerability audit.
  Its 61 browser cases were skipped by the normal opt-in boundary; the selected
  browser checks above executed separately.
- Documentation validation passed: 203 Markdown files and 1084 local links.
  JavaScript syntax and changed-file whitespace checks passed.

## Delivery and acceptance

Base: `8e6c3c4477da7824149664c48ce9f3816cd76745` (monitoring evidence closure).
Reviewed product commit: `a1b78314d274c259f08776a1f0cad389d336f90b`.

[Product PR #352](https://github.com/tov-vl/gold-src-ops/pull/352) is merged at
`cd1563d3b1e181ffe20875236bb1d7f8fa5eb1f7`. Full
[PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38591655522) and
[main CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38593161304) passed.
The [Web-only v2.77 release](v2.77-readiness.md) passed candidate publication,
independent production runtime/browser/telemetry acceptance and digest-preserving
stable promotion. The other eight containers, including both databases, and
accepted monitoring configuration were preserved.

RU/EN natural busy responses showed a ten-second countdown, blocked early Enter,
preserved input through ranking refresh and enabled an explicitly manual retry.
No automatic request was made. No-JavaScript search also passed. New-instance
metrics matched all eight browser submissions: six successes and two busy.
Synthetic tests retain the deterministic timing and lifecycle evidence.
Game visual checks, two-player play, weekly backup and personal notification
delivery remain independent. See the [release process](release-process.md).
