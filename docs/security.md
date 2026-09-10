# GoldSrcOps Security Model

This document defines the implemented security boundary for the GoldSrcOps
control plane.

## Trust Model

- GoldSrcOps.Api is a resource server. GoldSrcOps.Web is a confidential OIDC
  client and server-side BFF. Neither owns user accounts, passwords, or token
  issuance.
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

Lifetime validation uses an explicit 30-second clock skew. The production host
preflight requires synchronized time, so this allowance covers small clock
differences without retaining the framework's implicit five-minute window.

For local development only, `dotnet user-jwts` creates project-specific
tokens and keeps the signing key in the developer's User Secrets store. Local
tokens must never be accepted by a production deployment.

### Web BFF Session

The protected Blazor static-SSR pages use the OIDC authorization-code flow with
PKCE and a cookie session. The identity provider must issue an ID token and API
access token carrying the same configured application-role claim. Web applies
its own `Reader` and `Operator` policies to the ID-token principal and sends the
access token to the API only from server-side `HttpClient` calls. The Web
`Operator` policy requires both the exact role and a stable `sub` claim.

`SaveTokens` stores tokens in authentication properties, but the cookie handler
uses a bounded server-side ticket store. The browser cookie therefore contains
an opaque random session key rather than serialized tokens. The cookie is
HTTP-only, SameSite Lax, secure in Production, non-sliding, and expires after 55
minutes. Logout is a POST protected by antiforgery validation. Remote failures
use a generic error page and do not expose provider or token details.

The initial ticket store is process-local and limited to 1,024 sessions. The
reference deployment runs one Web instance, and any Web restart intentionally
logs every user out. This avoids introducing a distributed session dependency
for the MVP; horizontal Web scaling requires a shared encrypted ticket store
first.

A Kestrel-backed Playwright test verifies this boundary in a real browser. It
places recognizable fake JWT sentinels in the server-side authentication
ticket, renders the protected server, incident, command-history, and dead-letter
routes, and asserts that response bodies, DOM content, local storage, and
session storage expose no token. Recognizable command and dead-letter payload
sentinels independently verify that those raw payloads are not rendered. The
test also requires one opaque `__Host-GoldSrcOps.Web` cookie with `HttpOnly`,
`Secure`, `SameSite=Lax`, and root-path attributes. Its sign-in fixture is
registered only inside the test host and cannot be enabled in the production
application.

The guarded Web `say` and restart forms add defense in depth at the browser
boundary. Their POST endpoints independently require the Web `Operator` policy
and antiforgery validation. A bounded process-local confirmation is random,
short-lived, bound to subject, server, and command action, and consumed once
before the mutation request; it stores no message, credential, or token. It
reduces accidental duplicate submissions and blocks cross-command token reuse,
but is not an idempotency guarantee. An uncertain API outcome is never retried
automatically and directs the operator to durable command history.

Production startup fails unless authentication uses an HTTPS authority, a
file-backed client secret, and a persistent X.509-protected Data Protection key
ring. The Web container receives none of the API database, RCON, backup, or
observability secrets. When Web authentication is disabled, both its login
endpoint and protected routes fail closed with `404`.

Startup validation requires issuer and audience configuration. Metadata retrieval
must use HTTPS outside Development. There is no runtime
`Authentication:Disabled` switch, and tests replace authentication only inside
the test host created by `WebApplicationFactory`.

### Configuration

The bearer scheme uses the standard `Authentication:Schemes:Bearer` section.
Production configuration supplies `Authority`, an indexed `ValidAudiences`
entry, and `RoleClaimType`, or equivalent valid issuer and audience settings,
for the external identity provider. `RoleClaimType` is the exact JWT claim name that
contains the application roles. A collision-resistant URI claim name such as
`https://identity.example.com/claims/roles` keeps the contract independent of
provider-specific defaults.

When `RoleClaimType` is absent, local and test hosts retain the framework
`ClaimTypes.Role` default. The production Compose contract requires an explicit
value. Startup rejects an empty value or surrounding whitespace so a typo
cannot silently deny every Reader and Operator request.

### Reference Production Identity Baseline

The reference deployment uses Auth0 as its external identity provider. Its
non-secret resource-server contract is:

| Setting | Value |
| --- | --- |
| Authority | `https://dev-gz6ky0cyuxbeky0c.us.auth0.com/` |
| Audience | `https://api.goldsrcops.com` |
| Role claim | `https://goldsrcops.com/roles` |
| Application roles | `Reader`, `Operator` |

On 2026-08-31, the dedicated database identity completed a passkey-backed login
and received an access token with the expected issuer, audience, subject, and
`Operator` role claim. The native operator client accepts only the Auth0
database connection. Its Google social connection is disabled, and the retained
legacy Google identity has no application role. Public database sign-up is
disabled; account creation and role assignment remain explicit operator actions.

This verifies identity-provider configuration and token issuance. It does not
yet prove authorization through the public GoldSrcOps route. Reader access,
Operator access, missing-role, expired-token, wrong-issuer, and wrong-audience
behavior remain target-environment checks after the database recovery and
migration gates allow the production runtime to start.

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

The production identity provider and JWT bearer configuration map the
provider's application-role claim to ASP.NET Core roles through
`RoleClaimType`. Role names use the exact `Reader` and `Operator` casing.

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
| `GET /api/public/status` | Anonymous |
| `GET /api/alert-delivery/dead-letters` | `Reader` |
| `GET /api/alert-delivery/dead-letters/{eventId}` | `Reader` |
| `POST /api/alert-delivery/dead-letters/{eventId}/replay` | `Operator` |
| `GET /api/alert-delivery/replays/{requestId}` | `Reader` |
| `PUT /api/servers/{id}/credentials/rcon` | `Operator` |
| `GET /api/servers/{id}/credentials` | `Reader` |
| `POST /api/servers/{id}/commands/...` | `Operator` |
| `GET /api/servers/{id}/commands` | `Reader` |
| `GET /api/commands/{id}` | `Reader` |
| `POST /operator/servers/{id}/commands/say` on Web | `Operator` plus antiforgery and one-time confirmation |
| `POST /operator/servers/{id}/commands/restart/queue` on Web | `Operator` plus antiforgery, readiness refresh, and one-time command-bound confirmation |
| `GET /metrics` | `Reader` |
| `GET /openapi/{documentName}.json` in Development | `Reader` |
| `GET /health/live` | Anonymous |
| `GET /health/ready` | Anonymous |

The background dispatcher acts only on commands that were already queued through
an `Operator` endpoint. The persisted token subject remains the audit identity;
there is no separate public dispatch operation.

Health probes remain anonymous so container and platform probes do not need an
operator token. The public status endpoint is also anonymous, but returns only
aggregate state, enabled-server counts, open incidents for enabled servers, and
the latest enabled-server observation time. It excludes server addresses,
names, provider identifiers, command data, and observability endpoints, and its
response is cached server-side for 15 seconds. Prometheus must be configured
with a Reader token when scraping `/metrics`.

## HTTP Behavior

- A missing, expired, malformed, incorrectly signed, or wrong-issuer/audience
  token returns `401 Unauthorized` with the bearer challenge.
- A valid token whose principal does not satisfy the endpoint policy returns
  `403 Forbidden`.
- Authorization failures must not reveal token contents, expected secrets, or
  protected resource details.

The target-environment authorization matrix is implemented by
`tools/smoke/oidc-live.ps1` and documented in `docs/smoke-test.md`. It accepts
tokens only as `SecureString` values or masked prompts, disables redirects, and
does not read response bodies or token claims. Its optional sanitized evidence
file must remain outside the repository.

The production matrix passed against `v2.3.0-rc.4` at
`2026-08-31T20:58:21Z`. Anonymous, expired, foreign-issuer, and wrong-audience
requests returned `401` with a Bearer challenge. Reader dashboard and metrics
requests returned `200`, while Reader mutation and valid missing-role requests
returned `403`. Auth0 access-token lifetimes were restored to `3600/3600` after
the bounded expiration check, and the dedicated test identity retained only the
`Reader` role. Tokens and sanitized target evidence remain outside Git.

The expanded production matrix passed against `v2.4.0-rc.3` from revision
`cb4bf4f` at `2026-09-06T12:58:43Z`. It repeated the four `401` cases, Reader
read, metrics, mutation, and no-role cases, and added explicit Operator read and
authenticated invalid-mutation cases. All ten outcomes matched the HTTP
contract. Auth0 access-token lifetimes were restored to `3600/3600`, and the
dedicated test identity was restored to its direct `Reader` role. Tokens and
target-specific evidence remain outside Git.

The live foreign-issuer scenario uses an ephemeral RS256 key and therefore
tests a foreign issuer and foreign signing authority together. It demonstrates
that the public route fails closed, but does not attribute the rejection to one
validator in isolation. The unit test supplies the trusted signing key while
changing only issuer, audience, or lifetime and verifies each corresponding
validation failure separately.

## Verification

Unit and API integration tests prove that:

- anonymous requests receive `401` from Reader and Operator endpoints;
- a Reader can call read endpoints but receives `403` from every mutation;
- an Operator can call both read and mutation endpoints;
- dead-letter list, detail, and replay-record endpoints require Reader access;
- dead-letter replay requires Operator access and derives its audit identity
  from the authenticated subject;
- liveness, readiness, and the sanitized public status remain anonymous while
  metrics require Reader access;
- command requests cannot spoof `RequestedBy`;
- persisted `RequestedBy` comes from the authenticated `sub` claim;
- tokens without a usable subject receive `401` from protected endpoints;
- a configured namespaced role claim drives ASP.NET Core role membership;
- trusted-key tokens are rejected independently for expiration, wrong issuer,
  and wrong audience;
- the configured access-token clock skew remains bounded to 30 seconds;
- invalid role-claim configuration fails options validation;
- test authentication overrides exist only in the integration-test host.

## References

- [Configure JWT bearer authentication in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)
- [Map claims in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/authentication/claims?view=aspnetcore-10.0#name-claim-and-role-claim-mapping)
- [Policy-based authorization in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/authorization/policies?view=aspnetcore-10.0)
- [Manage development JWTs with dotnet user-jwts](https://learn.microsoft.com/aspnet/core/security/authentication/jwt-authn?view=aspnetcore-10.0)
