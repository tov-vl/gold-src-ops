# v2.78 - Partial player nickname search

Candidate published; stable release remains blocked by browser acceptance.
Both 2026-10-16 retries accepted addon 0.26.0 and both API search modes, then
stopped at the browser gate. Production was restored to addon 0.25.0, API v2.76 and Web
v2.77 with current player data preserved.

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
Full PR/main CI and local real Web/API/RCON integration passed. Candidate image
identity and production API acceptance passed. The first browser refusal lacked stage evidence. The diagnostic retry completed
RU with JavaScript, then failed at EN page loading with one unexpected request
failure (4 POSTs / 48 HTTP requests). The precise cause remains unproven; the
full browser matrix and stable promotion are not claimed. Separate forward/reverse
journals and both failure reports are preserved. See the
[installation, acceptance and rollback record](v2.78-readiness.md).
