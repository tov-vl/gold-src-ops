# GoldSrc RCON

GoldSrcOps executes operator commands through GoldSrc RCON after a command has
already been persisted as a `CommandExecution`. Dispatch is explicit today:
`POST /api/commands/{commandId}/dispatch` loads the queued command, marks it
`Running`, calls the infrastructure executor, and then stores `Succeeded` or
`Failed`.

## Execution Flow

1. `CommandExecutionService` builds the final command text:
   - `ChangeMap` -> `changelevel <map>`
   - `Restart` -> `_restart`
   - `Say` -> `say <message>`
   - `Raw` -> raw operator-provided text
2. `GoldSrcRconCommandExecutor` resolves the stored canonical RCON secret alias
   inside Infrastructure.
3. `GoldSrcRconClient` sends UDP `challenge rcon`.
4. The server returns a challenge token.
5. The client sends `rcon <challenge> "<password>" <command>`.
6. The executor maps the response to a sanitized command result.

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
through the credential endpoint before dispatch.

## Safe Failures

Dispatch fails before sending any UDP packet when the server has no RCON port,
the credential reference is missing, or the reference cannot be resolved.

The executor returns stable failure messages for:

- missing or unsupported secret references;
- RCON timeout;
- authentication failure;
- protocol errors;
- socket errors.

Failure messages and result summaries are sanitized so raw secrets are not
persisted. Run real RCON dispatch only against servers you own or administer.

## Current Limits

- Authentication and authorization are not implemented yet; expose the API only
  to trusted clients.
- IPv4 endpoints only, matching the current A2S client.
- One command is dispatched per explicit API request.
- Split or multi-packet RCON responses are not implemented yet.
- Passwords containing double quotes are rejected by the protocol layer.
- Per-server dispatch serialization and structured command logs are planned as
  the next hardening step.
