# GoldSrcOps v2.123

The downloadable Operator health report now includes polling worker progress,
cycle timestamps, completion age, budget and the three existing warning states.
All three health sources are fetched once, concurrently, with independent
Unknown/Stale handling and request cancellation. Fixed first checks and the
polling runbook travel with the UTF-8 attachment.

The existing path, filename, no-store/nosniff and bounded output remain compatible.
R1 Web-only delivery; API, receiver, monitoring policy, permissions, game/agent
and Archive cadence are unchanged. Released and installed:
[v2.123.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.123.0).

Required CI, independent artifact/runtime and actual authenticated download
acceptance passed. Only Web was recreated; fourteen other containers and
monitoring configuration were preserved. Stable reused all candidate digests
without rebuilding.
