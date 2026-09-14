# Sandbox AMX Mod X/ReAPI Game-Event Producer

This sample is the local-only producer side of the GoldSrcOps spool v1
contract. It observes completed Counter-Strike rounds through ReAPI and writes
one anonymous `round.ended` record for the companion .NET Worker. It performs
no network access and owns no OAuth credential, HTTP retry, player identity,
chat, address, RCON, or provider data.

The plugin is disabled by default:

```text
goldsrcops_events_enabled 0
goldsrcops_spool_incoming "addons/amxmodx/data/goldsrcops-spool/incoming"
```

The incoming path must already exist, must be relative to the game mod
directory, and must be restricted to the game-server owner. The plugin refuses
absolute paths, parent traversal, unsupported characters, missing directories,
invalid map names, oversized records, and files for which owner-only `0600`
permissions cannot be set.

For each observable `RG_RoundEnd`, the producer counts connected players and
bots while excluding HLTV, creates a lowercase UUID v4 record ID, formats the
occurrence time directly from Unix time as UTC, writes the complete payload to
`<recordId>.tmp`, flushes and closes it, verifies its byte count, and renames it
to `<recordId>.json` in the same directory. Setup and restart pseudo-rounds are
ignored, and one successful record is emitted at most once per round.

Compile the source with the pinned, hash-verified sandbox toolchain:

```powershell
pwsh -NoProfile -File ./tools/smoke/amxx-game-event-producer.ps1
```

The compiled artifact is written below ignored `artifacts/`; it is not an
installation package. The script pins AMX Mod X `1.10.0.5481` and ReAPI
`5.24.0.300`. The fixture in `fixtures/` is consumed by the .NET spool parser
tests so both sides share the same strict contract.

AMX Mod X exposes buffered `fflush` and close operations but no portable
filesystem sync primitive. This sandbox therefore proves complete-file process
handoff, not survival across host power loss. Production installation,
directory ownership, host compatibility, and crash/power-loss acceptance need
their own rollout evidence.
