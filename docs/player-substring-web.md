# Player search modes in Web

This local slice follows [addon v2](player-substring-addon.md) and
[API modes](player-substring-api.md). It adds a native search-mode selector to
`/leaderboard` with RU/EN copy and no new feature flag, database storage or
browser request mechanism. Production is unchanged.

## Behavior and compatibility

- Exact nickname is the initial mode. It remains case-sensitive and allows
  short names. The BFF keeps its legacy JSON body containing only `name`.
- Part of nickname requires at least three Unicode scalars, within 31 UTF-8
  bytes. It sends `match: contains` to the fixed server-side API route.
  Matching uses the same ASCII/Russian map as addon/API, including the distinct
  U+0401/U+0451 pair; other alphabets, normalization, transliteration and wildcards
  are not added.
- Each returned name is independently bounded, canonical and matched against the
  query in the selected mode before rendering. Actual spelling and duplicate
  records are preserved. Existing freshness, score/rank/order, body bounds,
  timeout, cancellation and retry checks remain in force.
- Exact and partial searches have separate empty/invalid guidance. Partial
  truncation asks the player to narrow the query. Short partial input remains
  editable so the player can choose exact search without retyping.
- A valid partial query rejected by an older API displays unavailable. It never
  becomes an empty result, stale rows or an automatic exact retry. New Web must
  follow addon v2 and API rollout; rollback starts with Web.

The selector uses the existing antiforgery-protected static SSR form. Missing
`Model.Match` on an older form defaults explicitly to exact; an empty or unknown
submitted value stays invalid and never reaches the API. This explicit default
is needed because form mapping can leave an omitted property null despite its
initializer. The selected mode and normalized name survive POST responses,
including busy/empty/error, and remain intact when only the leaderboard refreshes.
A fresh full GET starts with the default mode; no persistent preference is added.

The countdown script is unchanged. Changing the selector or nickname while busy
cannot reset its deadline or bypass the submit handler. Only the manual submit
uses the current selection after expiry. Without JavaScript, the ordinary POST
still works and the API/addon enforce shared admission.

No search input is added to URLs, redirects, browser storage, logs or metric
labels. Browser code never calls the API. Public output remains escaped, uses
no-store/no-referrer, and contains no identity keys. Existing duration metrics
keep their fixed operation and outcome labels.

## Verification

Focused service/SSR tests cover mode validation, Unicode scalar and fixed-case
behavior, actual returned names and duplicates, malformed responses in both
modes, old forms, old API rejection, request serialization, short exact names,
RU/EN rendering, antiforgery and request/response bounds.

The existing five browser scenarios now exercise both modes at 1440/390/320
pixels, partial search at 200% text size, mode preservation during leaderboard
refresh, exact-to-partial switching during countdown, early Enter/submit blocking,
manual retry and both modes without JavaScript. They also retain URL/storage,
no direct API calls, duplicates, truncation and page-restore checks. Local
screenshots use synthetic fixtures.

Results on 2026-10-15:

- All 83 focused service/SSR lookup tests passed.
- All five expanded browser scenarios passed with the pinned Playwright Chromium
  headless shell. No browser scenarios were skipped in that focused run.
- Full local Quality Gate passed: audit restore, format verification, build with
  zero warnings/errors, 1247 tests (753 UnitTests, 440 WebTests, 54 AlertReceiver)
  and no known vulnerable direct/transitive packages across 13 projects.
- The standard solution run skipped 61 opt-in browser tests; the five affected
  search scenarios were run separately as above. The remaining browser suite
  and Container Smoke were not rerun locally.
- Existing RCON timing tests passed in this full run without source changes.
  This does not establish or fix the cause of the previous API-stage timeouts.
- Changed documentation links and whitespace checks passed. Desktop/mobile
  screenshots were inspected; they are synthetic local evidence only.

Browser transport uses an API fixture, not a running addon/API deployment.
Full combined transport, required CI and production acceptance remain separate
gates. No push, PR, remote CI, publication or deployment was performed.
