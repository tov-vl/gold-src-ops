# v2.67 - Public status language

Published and accepted on 2026-10-12:
[v2.67.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.67.0).

The public status page and shared public navigation/footer now follow the
existing RU/EN URL choice. Visitors retain language between status, play and
leaderboard pages, and retain the selected 24-hour or 7-day history window when
switching language. Status, error, chart and clipboard messages are translated.
The mobile status heading adapts to wider system fonts at 320px.

Russian remains the default; ?lang=en selects English. Ordinary navigation
works without JavaScript. No cookies, browser storage or request culture change
is introduced. Server names, map names, connection commands and UTC remain
unchanged. The player-guide section link preserves its page and language.

Only Web was recreated. Six other services and full mount/volume identities
were preserved. API, database, game, addon, backup and authentication contracts
remain unchanged. Stable references reuse accepted candidate digests.

Local tests, final full CI, independent publication checks and production
RU/EN acceptance passed. See [readiness](v2.67-readiness.md) for exact identities,
the corrected Linux font overflow, evidence and remaining independent gates.
