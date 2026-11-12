using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Api.Endpoints;

internal static class RetentionHealthEndpoints
{
    public static void MapRetentionHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/operator/retention-health", async (RetentionHealthService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var value = await service.ReadAsync(cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new RetentionHealthResponse(value.EvaluatedAtUtc, value.ObservationState, value.ObservedAtUtc,
                value.LastCycleStartedAtUtc, value.LastCycleFinishedAtUtc, value.CompletionAgeSeconds, value.CycleBudgetSeconds,
                value.WarningsState, value.WarningsObservedAtUtc, [.. value.Warnings.Select(static item => new RetentionWarningResponse(item.Name, item.State))],
                value.LastSuccessfulCycleAtUtc, value.LastFailureAtUtc, value.LastCycleDurationSeconds, value.ConsecutiveFailures, value.InProgress));
        }).RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy).WithName("GetRetentionHealth").WithTags("Operator");
    }
}
