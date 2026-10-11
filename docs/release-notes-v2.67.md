# v2.67 - Public status language

Release candidate preparation; not yet published or accepted.

The public status page and shared public navigation/footer now follow the
existing RU/EN URL choice. Visitors retain language between status, play and
leaderboard pages, and retain the selected 24-hour or 7-day history window when
switching language. Status, error, chart and clipboard messages are translated.

Russian remains the default; ?lang=en selects English. Ordinary navigation
works without JavaScript. No cookies, browser storage or request culture change
is introduced. Server names, map names, connection commands and UTC remain
unchanged. The player-guide section link now preserves its page and language.

Only Web is affected. Operator content, authentication, API, game, addon,
backup and durable state retain their contracts. See
[readiness](v2.67-readiness.md) and [scope](v2.67-public-status-language.md).
