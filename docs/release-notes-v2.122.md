# GoldSrcOps v2.122

Add a read-only Polling health page in the Operator portal and an additive
Operator-only API. Worker state, cycle timestamps, completion age and the
existing observation budget remain separate from the three warning evaluations.
Missing, stale or duplicate active sources are never presented as healthy.

Static Refresh, fixed first checks and the existing runbook work through the
server-side BFF. Tokens, source identities and private provider material remain
outside browser output. Reads have fixed deadlines and size/sample bounds.

R1 API/Web-only release. No monitoring policy, game/agent, schema, credential,
provider or hourly :53 Archive schedule change. Candidate publication and
independent production/browser acceptance are required before stable promotion.
