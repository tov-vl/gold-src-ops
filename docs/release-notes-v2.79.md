# v2.79 - Partial-search action after an empty exact result

Released on 2026-10-16. The product change is merged through
[PR #363](https://github.com/tov-vl/gold-src-ops/pull/363); production accepted
the verified v2.79 Web candidate. API and addon remain on v2.78.

- An empty exact search offers "Искать по части ника" / "Search by part of
  nickname" when the preserved input is valid for partial search.
- The action keeps the nickname, selects contains mode and submits only when
  explicitly activated. Short nicknames below three Unicode scalars do not
  receive an unusable shortcut.
- Shared cooldown, busy countdown and manual retry remain intact. The action
  works with keyboard navigation and without JavaScript in both languages.
- This is a Web-only installation. API/addon, data schemas, game runtime,
  monitoring and backup policy remain unchanged.

Full product PR and main CI passed, including browser and container smoke.
[Candidate CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38934441725) and
[stable CI](https://github.com/tov-vl/gold-src-ops/actions/runs/38937253827) passed
all eleven jobs on the first attempt. Stable references preserve all three
candidate digests without rebuilding. The signed
[v2.79.0 release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.79.0)
points at product revision `8b2d929`.

Production RU/EN acceptance passed with and without JavaScript: keyboard
activation, shared cooldown, manual retry, widths 320/390/1440 and 200% text.
Ten search POSTs produced eight fresh and two expected busy outcomes in
78.794 seconds, with 82 HTTP requests and no browser errors. New-instance
metrics matched within the 90-second reconciliation budget; all rules were
healthy and no Web read-failure warning was active.

Independent checks confirmed healthy Web, preserved mounts, the other eight
containers, seventeen protected files and all non-Web deployment bytes.
Rollback was not required. This bounded Web acceptance does not replace game
visual checks or two-player gameplay.
See the [installation, acceptance and rollback plan](v2.79-readiness.md).
