# v2.66 Release Notes

Released on 2026-10-11 as [v2.66.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.66.0).
The accepted candidate digests were promoted without rebuilding.

The public player guide and top-10 leaderboard support Russian and English.
The RU/EN links preserve language between these pages and during status updates,
including normal navigation without JavaScript. Translated content includes
instructions, rank names, errors, stale-data notices and clipboard feedback.

Language belongs to the URL (`?lang=en`), with Russian as the default. It is
independent from the in-game language and introduces no browser storage or
cookies. Player names, maps, connection commands and numeric results keep their
original values. Refresh retains its anonymous, bounded SSR transport and
rejects responses in a different language.

This is a Web-only release. Game runtime, addon, public API contracts, durable
data, authentication and backup scheduling are unchanged. The deferred in-game
visual acceptance remains open.

Local Quality Gate passed all 963 ordinary tests; all 43 browser cases passed.
Production acceptance covered RU/EN, mobile layouts, clipboard, real-minute
refresh on both pages and navigation without JavaScript. Only Web was
recreated; six other services and their mounts were preserved.

See [contract](v2.66-public-language.md) and [readiness](v2.66-readiness.md).
