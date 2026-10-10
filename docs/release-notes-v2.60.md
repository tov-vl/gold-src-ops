# GoldSrcOps v2.60 Release Notes

Status: implementation in progress; publication and target acceptance pending.

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
candidate/stable digests and production evidence will be recorded in
[readiness](v2.60-readiness.md). See [contract](v2.60-play-status-refresh.md).
