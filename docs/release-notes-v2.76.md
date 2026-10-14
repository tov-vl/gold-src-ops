# v2.76 - Exact-name player search

Accepted on 2026-10-14. Stable [v2.76.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.76.0)
reuses the accepted candidate digests without rebuilding.

- The public leaderboard can search the saved-player index by exact nickname,
  including players outside the top ten. Up to ten matches are shown; identical
  names remain separate results without exposing private player identifiers.
- The RU/EN form works with and without JavaScript, supports keyboard input,
  mobile widths and 200% text. Top-ten refresh preserves search results and does
  not repeat a search. Nicknames stay out of URLs.
- Bounded lookup, cooldown, cache and explicit busy/unavailable states constrain
  load. Separate API/Web flags allow ordered activation and rollback.
- Addon 0.25 persists lookup activation through the verified package/receipt and
  backup-baseline workflow. Configuration rollback preserves current player data.

Production addon, API and Web acceptance passed; five other containers, mounts,
the telemetry agent and monitoring configuration were preserved. A preparation
RCON probe caused an own-address ban; the first claim refused before stopping the
game. A separately approved narrow unban restored local A2S and a new claim was
accepted. No database migration or vault restore was performed.

Search is observed through existing metrics; warning policy is unchanged.
Production contained one matching saved name. Duplicate, below-top-ten and full
index cases are covered by isolated tests. Game-client visual checks, two-player
play and the first successful weekly backup are not claimed by this release.
See [readiness and incident evidence](v2.76-readiness.md).
