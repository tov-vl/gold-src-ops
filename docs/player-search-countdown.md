# Player-search retry countdown

Implemented locally on 2026-10-14. Production still has the previously accepted
manual busy message; publication and deployed countdown acceptance are pending.

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

## Delivery and remaining gates

Base: `8e6c3c4477da7824149664c48ce9f3816cd76745` (monitoring evidence closure).
The reviewed product commit is recorded in the delivery handoff; no candidate
revision, tag or new production digest is claimed by this local record.

Product push/PR, full required CI, squash merge and post-merge CI are still
pending. A subsequent Web-only release must freeze the exact merged revision,
unused candidate/stable tags, verified image digest and fresh Web rollback digest
before changing production. Preserve the other six containers and the accepted
monitoring configuration. Use the [release process](release-process.md).

Production acceptance must exercise an ordinary search followed by a bounded
busy response, observe the localized countdown and manual retry, and verify
search telemetry without injecting a failure. Synthetic tests are the evidence
for deterministic timing and lifecycle cases. Game visual checks, two-player
play, weekly backup and personal notification delivery remain independent.
