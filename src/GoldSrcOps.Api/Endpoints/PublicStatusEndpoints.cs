using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GoldSrcOps.Api.Endpoints;

public static class PublicStatusEndpoints
{
    internal const string CachePolicyName = "PublicStatus";

    public static RouteGroupBuilder MapPublicStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/public")
            .WithTags("Public");

        group.MapGet("/status", GetStatusAsync)
            .AllowAnonymous()
            .CacheOutput(CachePolicyName)
            .WithName("GetPublicStatus");

        return group;
    }

    private static async Task<Ok<PublicStatusResponse>> GetStatusAsync(
        MonitoringReadService monitoring,
        CancellationToken cancellationToken)
    {
        var result = await monitoring.GetPublicStatusAsync(cancellationToken);
        return TypedResults.Ok(Map(result));
    }

    private static PublicStatusResponse Map(PublicStatusDto status) =>
        new(
            MapState(status.State),
            status.MonitoredServers,
            status.OnlineServers,
            status.ServersRequiringAttention,
            status.OpenIncidents,
            status.LastObservedAtUtc);

    private static string MapState(PublicStatusState state) => state switch
    {
        PublicStatusState.Operational => "operational",
        PublicStatusState.Degraded => "degraded",
        _ => "unknown"
    };
}
