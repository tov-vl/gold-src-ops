using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Api.Endpoints;

internal static class CredentialExpiryEndpoints
{
    public static void MapCredentialExpiryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/operator/credential-expiry", (CredentialExpiryService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = service.Read(cancellationToken);
            return TypedResults.Ok(new CredentialExpiryResponse(result.EvaluatedAtUtc,
                [.. result.Entries.Select(static item => new CredentialExpiryEntryResponse(item.Key, item.State, item.ExpiresAtUtc, item.VerifiedAtUtc, item.RemainingDays))]));
        }).RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy).WithName("GetCredentialExpiry").WithTags("Operator");
    }
}
