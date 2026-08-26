# GoldSrcOps Security Model

This document defines the implemented security boundary for the GoldSrcOps
control plane.

## Trust Model

- GoldSrcOps is a resource server. It validates access tokens but does not own
  user accounts, login flows, passwords, refresh tokens, or token issuance.
- A production deployment uses an external OAuth 2.0 / OpenID Connect identity
  provider to issue JWT access tokens.
- The current deployment has one administrative domain. Authenticated
  operators can act on every registered server; tenant and per-server access
  control are out of scope.
- HTTPS is required outside local development because bearer tokens grant access
  to the API while they are valid.
- RCON passwords remain outside PostgreSQL and API contracts as described in
  `docs/rcon.md`.

## Authentication

GoldSrcOps uses ASP.NET Core JWT bearer authentication. The API validates
the token signature, issuer, audience, and lifetime before creating an
authenticated principal. Production tokens must be issued through a standards-
based OAuth 2.0 or OpenID Connect flow; GoldSrcOps must not expose a token-issuing
endpoint or create production JWTs itself.

For local development only, `dotnet user-jwts` creates project-specific
tokens and keeps the signing key in the developer's User Secrets store. Local
tokens must never be accepted by a production deployment.

Startup validation requires issuer and audience configuration. Metadata retrieval
must use HTTPS outside Development. There is no runtime
`Authentication:Disabled` switch, and tests replace authentication only inside
the test host created by `WebApplicationFactory`.

### Configuration

The bearer scheme uses the standard `Authentication:Schemes:Bearer` section.
Production configuration supplies `Authority` and `Audience`, or equivalent
valid issuer and audience settings, for the external identity provider.

Create a short-lived local Operator token before starting the Development host:

```powershell
.\tools\dev\new-local-jwt.ps1 `
  -Name local-operator `
  -Role Operator `
  -ValidFor 1d
```

The ignored `appsettings.Local.json` file is loaded only by the Development
host. The JWT signing key remains in User Secrets; neither artifact belongs in
source control.

## Principal Identity

Every accepted access token must contain a stable subject identifier. The
OAuth/OpenID Connect `sub` claim is the application audit identity and must fit
within `CommandExecution.MaxRequestedByLength`. Token validation rejects a
missing, blank, or oversized subject before the principal can access any
protected endpoint.

Command and dead-letter replay request contracts do not accept `requestedBy`.
Both use cases derive their persisted audit identity from the authenticated
principal's subject. Responses expose `RequestedBy` only as audit metadata; a
client-supplied JSON property cannot override it.

Display names are not used as audit identity because they can change and need
not be unique. Access tokens, raw claims collections, and authorization headers
must not be written to logs or command history.

## Authorization Policies

The application uses policies rather than authorization checks inside endpoint
handlers.

| Policy | Accepted application role | Purpose |
| --- | --- | --- |
| `Reader` | `Reader` or `Operator` | Inspect server state, history, incidents, dead letters, replay records, command history, credential metadata, and metrics. |
| `Operator` | `Operator` | Register or modify servers, configure RCON credentials, queue commands, and replay reviewed dead letters. |

`Operator` includes read access through the `Reader` policy. ASP.NET Core does
not provide implicit role inheritance, so the `Reader` policy must explicitly
accept both roles.

The production identity provider and JWT bearer configuration must map the
provider's application-role claim to ASP.NET Core role claims. Role names use
the exact `Reader` and `Operator` casing.

The fallback policy is `Operator`. Every endpoint added later is therefore
operator-only until it is explicitly downgraded to `Reader` or marked
anonymous. This favors a safe failure over accidentally exposing a new control
operation.

## Endpoint Policy Matrix

| Endpoint | Policy |
| --- | --- |
| `POST /api/servers` | `Operator` |
| `PATCH /api/servers/{id}` | `Operator` |
| `POST /api/servers/{id}/enable` | `Operator` |
| `POST /api/servers/{id}/disable` | `Operator` |
| `GET /api/servers...` | `Reader` |
| `GET /api/incidents...` | `Reader` |
| `GET /api/dashboard/overview` | `Reader` |
| `GET /api/alert-delivery/dead-letters` | `Reader` |
| `GET /api/alert-delivery/dead-letters/{eventId}` | `Reader` |
| `POST /api/alert-delivery/dead-letters/{eventId}/replay` | `Operator` |
| `GET /api/alert-delivery/replays/{requestId}` | `Reader` |
| `PUT /api/servers/{id}/credentials/rcon` | `Operator` |
| `GET /api/servers/{id}/credentials` | `Reader` |
| `POST /api/servers/{id}/commands/...` | `Operator` |
| `GET /api/servers/{id}/commands` | `Reader` |
| `GET /api/commands/{id}` | `Reader` |
| `GET /metrics` | `Reader` |
| `GET /openapi/{documentName}.json` in Development | `Reader` |
| `GET /health/live` | Anonymous |
| `GET /health/ready` | Anonymous |

The background dispatcher acts only on commands that were already queued through
an `Operator` endpoint. The persisted token subject remains the audit identity;
there is no separate public dispatch operation.

Health probes remain anonymous so container and platform probes do not need an
operator token. They must continue returning only bounded health status, without
configuration, exception, or secret details. Prometheus must be configured with
a Reader token when scraping `/metrics`.

## HTTP Behavior

- A missing, expired, malformed, incorrectly signed, or wrong-issuer/audience
  token returns `401 Unauthorized` with the bearer challenge.
- A valid token whose principal does not satisfy the endpoint policy returns
  `403 Forbidden`.
- Authorization failures must not reveal token contents, expected secrets, or
  protected resource details.

## Verification

Unit and API integration tests prove that:

- anonymous requests receive `401` from Reader and Operator endpoints;
- a Reader can call read endpoints but receives `403` from every mutation;
- an Operator can call both read and mutation endpoints;
- dead-letter list, detail, and replay-record endpoints require Reader access;
- dead-letter replay requires Operator access and derives its audit identity
  from the authenticated subject;
- liveness and readiness remain anonymous while metrics require Reader access;
- command requests cannot spoof `RequestedBy`;
- persisted `RequestedBy` comes from the authenticated `sub` claim;
- tokens without a usable subject receive `401` from protected endpoints;
- test authentication overrides exist only in the integration-test host.

## References

- [Configure JWT bearer authentication in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)
- [Policy-based authorization in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/authorization/policies?view=aspnetcore-10.0)
- [Manage development JWTs with dotnet user-jwts](https://learn.microsoft.com/aspnet/core/security/authentication/jwt-authn?view=aspnetcore-10.0)
