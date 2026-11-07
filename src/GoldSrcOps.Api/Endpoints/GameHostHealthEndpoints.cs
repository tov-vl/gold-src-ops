using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Api.Endpoints;

internal static class GameHostHealthEndpoints
{
    public static void MapGameHostHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/operator/game-host-health", async (GameHostHealthService service, HttpContext context,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = await service.ReadAsync(cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new GameHostHealthResponse(result.EvaluatedAtUtc,
                [.. result.Components.Select(static item => new GameHostHealthComponentResponse(item.Component, item.State, item.ObservedAtUtc))],
                [.. result.Values.Select(static item => new GameHostHealthValueResponse(item.Metric, item.Role, item.Value))],
                result.Warnings is { } warnings ? new GameHostWarningsResponse(warnings.State, warnings.ObservedAtUtc,
                    [.. warnings.Items.Select(static item => new GameHostWarningResponse(item.Name, item.Component, item.State, item.Description))]) : null));
        }).RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy).WithName("GetGameHostHealth").WithTags("Operator");
    }
}
