# GoldSrcOps v2.126

The Operator diagnostic download now includes all six overview sources: game-host,
Archive, polling, recorded credential expiry, incident outbox and receiver.
Independent timestamps, minute precision, Unknown/Stale and disabled access are
preserved. Failures isolate one section; caller cancellation stops the download.
An empty queue never establishes provider delivery or email receipt.

The existing Operator-only URL, filename, UTF-8 and bounded read-only contract
remain compatible. Only Web is installed, with exact prior-image rollback.
Workers, permissions, providers, monitoring, game/agent and Archive cadence stay
unchanged. Released and installed as
[v2.126.0](https://github.com/tov-vl/gold-src-ops/releases/tag/v2.126.0).
Required CI, immutable publication and independent runtime/authenticated download
acceptance passed. Fourteen other containers were preserved; stable reused all
three candidate digests without rebuilding. See [scope and evidence](v2.126-complete-health-report.md).
