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
2. `GoldSrcRconCommandExecutor` resolves the stored `SecretReference` inside
   Infrastructure.
3. `GoldSrcRconClient` sends UDP `challenge rcon`.
4. The server returns a challenge token.
5. The client sends `rcon <challenge> "<password>" <command>`.
6. The executor maps the response to a sanitized command result.

Raw RCON passwords are not stored in PostgreSQL, returned by API contracts, or
written into command history.

## Secret References

`ServerCredential.SecretReference` is a pointer to a secret, not the secret
value itself. Supported local schemes:

- `env://VARIABLE_NAME`
- `config://Section:Key`
- `dev-secrets://goldsrcops/server/rcon`

`dev-secrets://goldsrcops/server/rcon` maps to the configuration key
`DevSecrets:goldsrcops:server:rcon`. In local development it can be supplied
with an environment variable:

```powershell
$env:DevSecrets__goldsrcops__server__rcon = "<your-rcon-password>"
```

An explicit environment-variable reference is also valid:

```json
{
  "secretReference": "env://GOLDSRCOPS_RCON_PASSWORD"
}
```

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

- IPv4 endpoints only, matching the current A2S client.
- One command is dispatched per explicit API request.
- Split or multi-packet RCON responses are not implemented yet.
- Passwords containing double quotes are rejected by the protocol layer.
- Command execution metrics and per-server dispatch serialization are planned
  as the next hardening step.
