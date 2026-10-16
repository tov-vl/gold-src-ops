# v2.79 - Partial-search action after an empty exact result

Prepared, not released. The product change is merged through
[PR #363](https://github.com/tov-vl/gold-src-ops/pull/363); production remains on
the accepted v2.78 Web image.

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
Candidate publication, production acceptance and stable promotion are pending.
See the [installation, acceptance and rollback plan](v2.79-readiness.md).
