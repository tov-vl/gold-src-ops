# GoldSrcOps v2.59 - Leaderboard Page Refresh

Stable [v2.59.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.59.0)
promoted from accepted rc.1 at `64233f4132aaf46caeb3524689508a5b4767d611`,
without rebuilding or repeating rollout.

- The public leaderboard updates results once a minute in a visible tab and
  supports manual refresh, preserving document, focus and join navigation.
- Age advances between reads. Failure makes old results stale; unavailable or
  expired data clear them. Hidden tabs pause reads; timeout and response bounds
  prevent overlapping or unbounded work. Normal SSR reload works without JS.
- Only Web was recreated. Six other services, complete mount/volume identities,
  existing API/addon/gameplay/save/scoring/schema, monitoring and backup retained.

941 .NET tests, 32 browser cases, full local quality, required CI and published
artifact checks passed. Independent production acceptance verified manual and
real-minute refresh, source-capture progression, layouts 1440/390/320px and the
anonymous browser boundary without reload, credentials or direct API calls.

Personal delivery, first weekly backup on 2026-10-11 at 04:00 MSK and deferred
game-client/two-player acceptance remain separate work. See
[readiness](v2.59-readiness.md) and [contract](v2.59-leaderboard-refresh.md).
