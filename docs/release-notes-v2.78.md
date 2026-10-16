# v2.78 - Partial player nickname search

Released on 2026-10-16. Production accepted addon 0.26.0, API and Web after
the browser context-close correction. All stable images reuse the existing
candidate digests without rebuilding; AlertReceiver runtime remains unchanged.

- RU/EN search forms offer an exact nickname or a part of it; exact remains the
  default. Partial search needs at least three Unicode scalars and retains the
  31-byte UTF-8 input limit.
- Partial matching uses a fixed ASCII/Russian case map. Results show the actual
  stored spelling, preserve duplicate names and return at most ten rows with a
  truncation marker. Wildcards, transliteration and Unicode normalization are
  not added.
- Exact and partial results have separate cache keys, with a shared cooldown and
  in-flight limit. Existing callers without a mode retain exact search.
- RU/EN forms keep manual retry, countdown behavior, antiforgery, no-JavaScript
  submission and the existing URL/storage/privacy boundaries.
- Addon 0.26.0 retains the existing vault schemas and enabled configuration. The
  release installs addon first, API second and Web last; rollback reverses this
  order and keeps current player data.

The product is merged in [PR #355](https://github.com/tov-vl/gold-src-ops/pull/355).
Full product PR/main CI and local real Web/API/RCON integration passed.
[PR #360](https://github.com/tov-vl/gold-src-ops/pull/360) corrected the browser
checker context-close race without changing candidate product code.

Production API acceptance passed seven POSTs in 33.92 seconds. The final browser
matrix passed all four RU/EN and JavaScript/no-JavaScript contexts, both modes,
320/390/1440 widths and 200% text: thirteen POSTs, 129 HTTP requests and no request,
page or console errors. New-instance metrics matched eleven success and two busy
outcomes. Vaults, reconciled backup baseline, unaffected containers and monitoring
configuration were preserved.

[Stable publication CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38901722201)
passed all eleven jobs, including independent checks of all three published
digests. The signed [v2.78.0 release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.78.0)
points at the same product revision as v2.78.0-rc.1.

Earlier refused attempts and verified rollbacks remain in the
[installation, acceptance and rollback record](v2.78-readiness.md). Local
reproduction proved the cleanup race and the corrected production run passed;
the old failure report still cannot uniquely identify its original cause.
Game-client visual checks and two-player gameplay remain separate from this
bounded Web/API search acceptance.
