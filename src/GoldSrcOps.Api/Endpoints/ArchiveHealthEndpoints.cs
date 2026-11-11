using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Api.Endpoints;

internal static class ArchiveHealthEndpoints
{
    public static void MapArchiveHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/operator/archive-health", async (ArchiveHealthService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var snapshot = await service.ReadAsync(cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new ArchiveHealthResponse(snapshot.EvaluatedAtUtc, snapshot.ObservationState, snapshot.ObservedAtUtc,
                snapshot.HasScheduledSuccess, snapshot.LastSuccessAtUtc, snapshot.LastSuccessAgeSeconds, snapshot.WarningsState,
                snapshot.WarningsObservedAtUtc, [.. snapshot.Warnings.Select(static item => new ArchiveWarningResponse(item.Name, item.State))]));
        }).RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy).WithName("GetArchiveHealth").WithTags("Operator");
    }
}
