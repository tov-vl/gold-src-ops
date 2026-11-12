using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Api.Endpoints;

internal static class PollingHealthEndpoints
{
    public static void MapPollingHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/operator/polling-health", async (PollingHealthService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var value = await service.ReadAsync(cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new PollingHealthResponse(value.EvaluatedAtUtc, value.ObservationState, value.ObservedAtUtc,
                value.LastCycleStartedAtUtc, value.LastCycleFinishedAtUtc, value.CompletionAgeSeconds, value.CycleBudgetSeconds,
                value.WarningsState, value.WarningsObservedAtUtc, [.. value.Warnings.Select(static item => new PollingWarningResponse(item.Name, item.State))]));
        }).RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy).WithName("GetPollingHealth").WithTags("Operator");
    }
}
