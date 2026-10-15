# v2.78 - Partial player nickname search

Candidate published; stable release blocked by addon acceptance. Production
retains addon 0.25.0, API v2.76 and Web v2.77. Reconciliation is pending.

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
Full PR/main CI and local real Web/API/RCON integration passed. Production partial
search and stable promotion are not claimed. Candidate CI and independent image
identity verification passed; the addon attempt returned to its predecessor,
with its failed-operation journal retained for reconciliation.
See the [installation, acceptance and rollback plan](v2.78-readiness.md).
