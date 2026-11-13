using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Api.Endpoints;

internal static class CommandDispatchHealthEndpoints
{
    public static void MapCommandDispatchHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/operator/command-dispatch-health", async (CommandDispatchHealthService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var value = await service.ReadAsync(cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new CommandDispatchHealthResponse(value.EvaluatedAtUtc, value.ObservationState, value.ObservedAtUtc,
                value.LastCycleStartedAtUtc, value.LastCycleFinishedAtUtc, value.CompletionAgeSeconds, value.CycleBudgetSeconds,
                value.WarningsState, value.WarningsObservedAtUtc, [.. value.Warnings.Select(static item => new CommandDispatchWarningResponse(item.Name, item.State))],
                value.LastSuccessfulCycleAtUtc, value.LastFailureAtUtc, value.LastCycleDurationSeconds, value.ConsecutiveFailures, value.InProgress));
        }).RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy).WithName("GetCommandDispatchHealth").WithTags("Operator");
    }
}
