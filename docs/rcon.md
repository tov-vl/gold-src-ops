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
9. The executor maps the response to a sanitized command result.
10. The dispatcher conditionally stores the final status only while the same
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

## Current Limits

- IPv4 endpoints only, matching the current A2S client.
- Split or multi-packet RCON responses are not implemented yet.
- Passwords containing double quotes are rejected by the protocol layer.
- Comprehensive structured command lifecycle logs are the next hardening step.
