# GoldSrcOps v2.57 Release Notes

Status as of 2026-10-09: [stable v2.57.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.57.0)
is published and addon/API/Web production acceptance passed.

## What Changed

- Anonymous Russian `/leaderboard` shows the authoritative saved `/topall`
  top ten: nickname, rank, kills, deaths and snapshot time.
- Addon 0.23.0 exports a bounded read-only frame. A default-off API worker reads
  it once per minute using existing server registration and RCON credentials.
- API serves only sanitized cached rows, with fresh/stale/unavailable states
  and a one-day retention ceiling. Browser visits never issue game commands.
- Responsive SSR table escapes names, identifies an empty ranking, handles
  unavailable data and links to `/play`; primary navigation and guide link back.

Scoring, rank thresholds, nVault saves and schema remain unchanged. There is no
new database history, player identity field, credential, public RCON or browser
bearer token. Runtime scope is addon/API/Web, with previous artifacts preserved
for rollback. See [readiness](v2.57-readiness.md) and
[the transport contract](v2.57-public-leaderboard.md).

## Verification And Limits

Parser/store/poller/API, SSR and isolated engine checks passed. Local Quality
Gate passed with 930 tests and no vulnerable packages; all 26 Chromium boundary
cases passed. All 11 candidate publication jobs and addon/API/Web target
acceptance passed. Source/API rows matched, the periodic snapshot advanced by
60 seconds, and production Chromium passed at 1440/390/320 pixels with no
errors or browser API requests. Five other services retained their identities.
All 11 stable jobs passed and all three references reuse accepted candidate
digests without rebuilding. Signed tags target
`7c83493a19c477555aa0709b85cbc17fa6244be7`; rollback was not needed.
Client v2.55 acceptance, two-player play, first weekly backup and personal
notifications remain separate tasks. This pilot makes no real-time or SLO claim.
