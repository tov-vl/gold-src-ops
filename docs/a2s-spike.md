# A2S Spike

## Goal

Validate the riskiest technical part of GoldSrcOps early: querying a live CS 1.6 / GoldSrc server over UDP and converting the binary A2S response into a typed model.

## Scope

The spike intentionally stays small and only covers `A2S_INFO`.

Implemented:

- UDP request to a game server query port.
- Timeout handling.
- `S2C_CHALLENGE` handling.
- Source-style `0x49` info response parsing.
- GoldSrc-style `0x6D` info response parsing.
- Basic typed output for name, map, players, max players, bots, protocol, game, version, and latency.
- Configurable server-name encoding.

Not implemented yet:

- `A2S_PLAYER`.
- `A2S_RULES`.
- Split packet reassembly.
- RCON.
- Persistence.
- Background polling.

## Decision Notes

This lives in a console app for now because it lets us test the protocol independently from API, database, and hosting concerns.
Once the behavior is stable, the client can move into `GoldSrcOps.Infrastructure` behind an application-level interface.

## Example

```powershell
dotnet run --project .\src\GoldSrcOps.A2SSpike -- server.csomod.com:27015 --timeout 5000 --encoding windows-1251
```

Use `--encoding windows-1251` when querying servers with Cyrillic names or metadata.
