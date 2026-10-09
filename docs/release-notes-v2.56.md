# GoldSrcOps v2.56 Release Notes

Status: implementation pending release verification and production acceptance.

## What Changed

- Added the anonymous Russian `/play` page for the current CS 1.6 fast-respawn
  pilot, weapon choice, saved ranks and first steps.
- Put Steam join, a copyable console command, current map/population and the
  observation timestamp together, using the existing public projection.
- Added truthful offline/unknown/unavailable states, Russian clipboard feedback,
  mobile layout and links from the primary navigation and home page.
- Listed `/menu`, `/guns`, `/profile`, `/settings`, `/maps` and `/help`, with
  explicit Steam-identity and unavailable-saving limits.

Only Web is affected at runtime. No new API, database migration, authentication,
worker, gameplay or backup policy is introduced. Publication and target evidence
will be recorded in [readiness](v2.56-readiness.md) before claiming a stable
release. The real Steam handler and in-game client are outside the browser tests;
client v2.55 acceptance and the first weekly backup remain separate.
