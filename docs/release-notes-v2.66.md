# v2.66 Release Notes

Status: pending release.

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

See [contract](v2.66-public-language.md) and [readiness](v2.66-readiness.md).
