# v2.75 - Leaderboard rank progress

Accepted on 2026-10-13. Stable [v2.75.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.75.0) reuses the accepted candidate without rebuilding.

- The public top-10 shows kills needed for the next rank and marks Legend as the highest rank.
- Progress uses the current leaderboard snapshot, retains its stale warning and updates together with the results. Unknown or inconsistent values omit progress.
- The guide and leaderboard share one Web display catalog. RU/EN, mobile, 200% text, keyboard and no-JavaScript browsing remain supported.

Web-only rollout preserved six other containers, all mounts and monitoring configuration. Gameplay, rank calculation, saved data and public API contracts are unchanged. Browser and runtime acceptance are separate from deferred game visual verification. See [readiness](v2.75-readiness.md).
