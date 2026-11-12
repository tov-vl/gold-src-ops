# GoldSrcOps v2.124

The Operator portal gains one operations health overview, combining existing
game-host, Archive, polling and recorded credential-expiry data. Each source
keeps independent evaluation/freshness and warning state, with fixed detail
links, explicit Unknown/Stale and ordinary Refresh without JavaScript.

Four reads start once in parallel. A source failure preserves the other cards;
request cancellation reaches all reads. Operator-only access, no-store and
server-side bearer handling remain intact. Credential dates retain metadata-only
meaning, and the existing three-source report remains a separate download.

R1 Web-only delivery with existing API contracts and exact prior Web rollback.
API/receiver, monitoring policy, permissions, providers, game/agent and Archive
cadence remain unchanged. Released and installed:
[v2.124.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.124.0).

Required CI, independent artifact/runtime and actual authenticated overview
acceptance passed. Only Web was recreated; all fourteen other captured
containers and protected monitoring configuration were preserved. Stable
promotion reused the candidate digests with all build steps skipped.
