# v2.74 - Player rank guide

Accepted on 2026-10-13. Stable [v2.74.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.74.0) reuses the accepted candidate without rebuilding.

- `/play#ranks` shows all five ranks and saved enemy-kill thresholds: 0, 25, 100, 250 and 500.
- `/leaderboard` links to the guide and explains cumulative rank versus relative top-10 position.
- RU/EN, keyboard navigation, mobile layouts and no-JavaScript browsing are supported. Existing localized names are reused; the Russian introduction no longer lists English rank names.

Web-only rollout preserved six other containers, all mounts and monitoring configuration. Gameplay, saved data and API contracts are unchanged. Browser and runtime acceptance are separate from deferred game visual verification. See [readiness](v2.74-readiness.md).
