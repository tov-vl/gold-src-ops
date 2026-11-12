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
cadence remain unchanged. Required CI, immutable publication and independent
runtime/browser acceptance gate stable promotion.
