# GoldSrcOps v2.60 Release Notes

Status: stable `v2.60.0` published and accepted on 2026-10-10 at
`3e3a118e4ef766b23055b02607f8b27979840f70`. [GitHub Release](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.60.0).

The player guide `/play` refreshes server status, map and population every
minute in a visible tab or on request. The last observation age grows between
responses. Failed refreshes or expired observations no longer present old map
and population as current. Confirmed offline, unknown and unavailable states
remain distinct, and a later successful response restores connection controls.

The page, focus and existing clipboard controls are preserved while the public
address can change on a verified response. Without JavaScript the manual link
reloads normal SSR. Reads are serialized, timed out and bounded, with no browser
credentials, protected API requests or storage.

R1, Web-only. API, addon, gameplay, statistics/preferences, schema, monitoring
and backup schedule retain their prior boundary. Exact source, immutable
candidate/stable digests and production evidence are recorded in
[readiness](v2.60-readiness.md). See [contract](v2.60-play-status-refresh.md).


Local Quality Gate passed all 944 .NET tests, 40 opt-in browser cases and all
three container smokes. Required product/main CI and all candidate/stable jobs
passed. Production manual and real-minute refresh, advancing observation,
actual clipboard, preserved document/focus and layouts 1440/390/320px passed.
Only Web was recreated; six other containers and full mount/volume identities
were preserved. Stable promotion reused all accepted digests without rebuild
or another rollout. Rollback was not required.

First weekly backup and deferred game-client/two-player checks remain separate.
