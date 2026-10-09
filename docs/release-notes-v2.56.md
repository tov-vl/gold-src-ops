# GoldSrcOps v2.56 Release Notes

Status as of 2026-10-09: stable
[`v2.56.0`](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.56.0)
published. The accepted Web digest is deployed at
[the public player guide](https://goldsrcops.com/play).

## What Changed

- Added the anonymous Russian `/play` page for the current CS 1.6 fast-respawn
  pilot, weapon choice, saved ranks and first steps.
- Put Steam join, a copyable console command, current map/population and the
  observation timestamp together, using the existing public projection.
- Added truthful offline/unknown/unavailable states, Russian clipboard feedback,
  mobile layout and links from the primary navigation and home page.
- Listed `/menu`, `/guns`, `/profile`, `/settings`, `/maps` and `/help`, with
  explicit Steam-identity and unavailable-saving limits.

Only Web was replaced at runtime. No new API, database migration,
authentication, worker, gameplay or backup policy was introduced. Six other
control-plane services retained exact container/image identities, start times
and restart counts. Healthy candidate metadata and zero unexpected Web
restarts passed target checks; rollback was not needed.

## Verification And Limits

[PR #287](https://github.com/tov-vl/gold-src-ops/pull/287), all required
[PR](https://github.com/tov-vl/gold-src-ops/actions/runs/37944860820) and
[main](https://github.com/tov-vl/gold-src-ops/actions/runs/37946392968) checks,
local Quality Gate (882 tests, no vulnerable packages), and all 24 Chromium
boundary cases passed. Production Chromium confirmed anonymous home-to-guide
navigation, exact advertised join values, real clipboard, token/storage
boundary and layout at 1440, 390 and 320 pixels, without page errors or browser
API requests. Desktop and mobile screenshots were inspected.

Signed candidate and stable tags target
`56527358c98cdfa83cbcb6575e7860d33bfc5bc1`. All jobs in
[candidate CI](https://github.com/tov-vl/gold-src-ops/actions/runs/37946405037)
and [stable CI](https://github.com/tov-vl/gold-src-ops/actions/runs/37948773938)
passed. Stable API, Web and AlertReceiver references reuse independently
verified candidate digests without rebuilding or another rollout.

The real Steam handler and in-game client were not launched by these checks.
Client v2.55 acceptance, two-player play, first weekly backup and personal
notifications remain separate tasks. The online acceptance sample is not an
uptime promise. See [readiness](v2.56-readiness.md) for exact digests and the
[product boundary](v2.56-player-guide.md) for unavailable-state behavior.
