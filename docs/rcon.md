# GoldSrc RCON

GoldSrcOps executes operator commands through GoldSrc RCON after a command has
already been persisted as a `CommandExecution`. Credential changes, command
queueing requires the `Operator` policy, and the command audit identity comes
from the authenticated token subject. A background dispatcher automatically
claims queued commands, calls the infrastructure executor, and stores
`Succeeded` or `Failed`.

## Execution Flow

1. `CommandExecutionService` validates the server and credential metadata and
   persists a `Pending` command.
2. `CommandDispatchBackgroundService` starts up to `MaxConcurrency` dispatch
   attempts per pass.
3. PostgreSQL atomically claims the oldest eligible command, marks it `Running`,
   and prevents another worker from claiming the same server.
4. `CommandDispatcher` builds the final command text:
   - `ChangeMap` -> `changelevel <map>`
   - `Restart` -> `_restart`
   - `Say` -> `say <message>`
   - `Raw` -> raw operator-provided text
5. `GoldSrcRconCommandExecutor` resolves the stored canonical RCON secret alias
   inside Infrastructure.
6. `GoldSrcRconClient` sends UDP `challenge rcon`.
7. The server returns a challenge token.
8. The client sends `rcon <challenge> "<password>" <command>`.
9. The client requires one valid `A2A_PRINT` response, then drains additional
   response datagrams until the configured quiet interval elapses.
10. The executor maps the assembled response to a sanitized command result.
11. The dispatcher conditionally stores the final status only while the same
    claim is still `Running`.

Raw RCON passwords are not stored in PostgreSQL, returned by API contracts, or
written into command history.

## Secret Aliases

The credential API accepts a `secretAlias`, not a configuration path or secret
value. GoldSrcOps validates the alias and stores a canonical
`rcon-secret://<alias>` reference in `ServerCredential.SecretReference`.

Aliases are limited to 128 ASCII letters, digits, `.`, `_`, and `-`, and must
start and end with a letter or digit. Separators used by configuration sections
are not allowed.

For the alias `server_rcon`, the resolver reads only the dedicated
`RconSecrets:server_rcon` configuration key. Configure it with Secret Manager:

```powershell
dotnet user-secrets set "RconSecrets:server_rcon" "<your-rcon-password>" `
  --project .\src\GoldSrcOps.Api
```

Or set the equivalent environment variable before starting the API:

```powershell
$env:RconSecrets__server_rcon = "<your-rcon-password>"
```

Both configuration providers produce the same dedicated key. Arbitrary
`env://`, `config://`, and `dev-secrets://` references are unsupported, so an
API caller cannot select unrelated application configuration such as a database
connection string. Existing credentials using the old schemes must be updated
through the credential endpoint before a queued command can execute.

## Safe Failures

Execution fails before sending any UDP packet when the server has no RCON port,
the credential reference is missing, or the reference cannot be resolved.

The executor returns stable failure messages for:

- missing or unsupported secret references;
- RCON timeout;
- authentication failure;
- protocol errors;
- socket errors.

Failure messages and result summaries are sanitized so raw secrets are not
persisted. Run real RCON dispatch only against servers you own or administer.

## Response Collection

The RCON client uses one end-to-end deadline beginning before DNS resolution and
ending only after the command response becomes quiet. Its UDP socket is
connected to the resolved IPv4 endpoint, so the operating system discards
datagrams from other addresses or ports during both the challenge and command
response phases.

Each valid command-response datagram must contain the GoldSrc connectionless
header followed by the `A2A_PRINT` response type. The client preserves each
chunk boundary, concatenates chunks in receive order, and trims the assembled
text only once. After the first response, each accepted datagram starts a new
quiet interval. No command is retried automatically.

Configuration lives under `Rcon`:

| Setting | Default | Valid range | Purpose |
| --- | ---: | ---: | --- |
| `TimeoutMilliseconds` | `3000` | positive integer | One deadline for resolution, challenge, command send, first response, and draining. |
| `MaxResponseLength` | `2000` | positive integer, capped at `2000` | Post-sanitization character limit persisted in command history. |
| `ResponseDrainMilliseconds` | `100` | `10` to `1000` | Quiet period used to infer the end of a legacy response. |
| `MaxResponseDatagrams` | `32` | `1` to `256` | Maximum datagrams accepted for one command response. |
| `MaxResponseBytes` | `65536` | `5` to `1048576` | Aggregate wire-byte ceiling, including each four-byte connectionless header. |

Invalid values for the three response-collection settings fail application
startup. Reaching either response ceiling, or reaching the overall deadline
while response datagrams keep arriving, produces a protocol failure instead of
persisting a known partial success.

The quiet interval is necessarily heuristic: legacy RCON responses carry no
response id, fragment count, sequence number, or completion marker. UDP loss,
reordering, duplication, or a final datagram delayed beyond the quiet interval
cannot be detected. The defaults are covered by synthetic timing tests; an
isolated local ReHLDS 3.14.0.857 capture produced 12 command-response
datagrams, 16,549 aggregate wire bytes, and an 8.206 ms maximum inter-datagram
gap for the read-only `cvarlist` command. The 100 ms default quiet interval is
retained. This single controlled capture does not remove the protocol limits
or guarantee equivalent timing under packet loss or production load.

## Serialization And Recovery

The claim query locks a registered server row with `FOR UPDATE SKIP LOCKED`,
marks one pending command `Running`, and returns the command id in the same
PostgreSQL statement. Multiple workers or API replicas can therefore execute
different servers concurrently while preserving one active command per server.

A command that remains `Running` longer than `InterruptedAfterSeconds` is marked
`Failed` with a stable interruption reason. It is not automatically requeued:
RCON has no idempotency key, so retrying after an unknown transport outcome could
repeat a restart, map change, or arbitrary raw command. An operator can inspect
the audit record and explicitly queue a new command when appropriate. A late
worker cannot overwrite the recovered status because completion checks both the
`Running` state and original claim timestamp.

Dispatcher settings live under `CommandDispatcher`:

- `Enabled` enables the hosted worker;
- `LoopDelayMilliseconds` controls the idle delay;
- `MaxConcurrency` bounds commands executed across different servers;
- `InterruptedAfterSeconds` defines the stale `Running` threshold;
- `RecoveryIntervalSeconds` controls stale-command scans.

`InterruptedAfterSeconds` must be greater than the configured RCON timeout or
the application rejects the configuration during startup.

## Lifecycle Logs

Each claimed command emits structured lifecycle events for dispatch start and
one terminal local outcome: completed, completion claim lost, or interrupted.
The events include `CommandId`, `ServerId`, `CommandType`, and `CommandStatus`;
terminal events also include `DurationMs`, and completed or lost-completion
events include `DispatchResult`.

The log contract intentionally excludes the command payload, rendered RCON
command text, requester, credential secret reference, password, and executor
response or failure text. Operators can correlate the safe identifiers with the
persisted `CommandExecution` audit record when details are needed.

## Guarded Live Smoke

Use `tools/smoke/rcon-live.ps1` for an end-to-end check against a server you own
or administer. The helper calls the authenticated API rather than the RCON
client directly, so it covers server and credential metadata, command queueing,
background dispatch, secret resolution, and terminal status persistence.

Run a preflight first:

```powershell
.\tools\smoke\rcon-live.ps1 `
  -ServerId "<registered-server-id>" `
  -AcknowledgeOwnedServer `
  -WhatIf
```

The script prompts for an Operator JWT without echoing it. Preflight reads the
registered server and credential metadata, verifies that the server is enabled
and has an RCON port, and prints the generated `say` command. `-WhatIf` prevents
the command POST.

Remove `-WhatIf` only after checking the target:

```powershell
.\tools\smoke\rcon-live.ps1 `
  -ServerId "<registered-server-id>" `
  -AcknowledgeOwnedServer
```

Live dispatch requires typing the same server id again. The helper only sends a
generated `say GoldSrcOps smoke <timestamp> <nonce>` command; it cannot send raw,
restart, or map-change commands. It reports only command id, server id, status,
and elapsed time. It never reads the RCON password or prints the JWT, credential
reference, response text, or failure text.

A local timeout does not prove that RCON execution did not happen. Inspect the
persisted command by id before deciding whether to queue another command.

## Current Limits

- IPv4 endpoints only, matching the current A2S client.
- Bounded collection supports one or more ordinary `A2A_PRINT` datagrams, but
  the legacy protocol still cannot prove completeness after UDP loss,
  reordering, duplication, or a final datagram delayed beyond the quiet window.
- Passwords containing double quotes are rejected by the protocol layer.
