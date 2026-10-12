# v2.71 - Bounded public API response reads

Public pages now apply their five-second API timeout to the entire response,
including its JSON body. When headers arrive but the body stalls, pages can
show the existing unavailable state at the deadline. Earlier caller cancellation
is preserved and no automatic retry is added.

In a local seven-second body-delay scenario, page response time decreased from
about seven to five seconds. This is a degradation improvement, not a claim of
faster ordinary loads. Only Web is affected; API contracts, database,
authentication and game runtime are unchanged.

Published and accepted on 2026-10-12: [v2.71.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.71.0). See
[readiness](v2.71-readiness.md) and [verification](v2.71-public-response-deadline.md).
