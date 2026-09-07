using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GoldSrcOps.Api.Endpoints;

public static class PublicStatusEndpoints
{
    internal const string CachePolicyName = "PublicStatus";
    internal const string A2sHistoryCachePolicyName = "PublicA2sHistory";

    public static RouteGroupBuilder MapPublicStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/public")
            .WithTags("Public");

        group.MapGet("/status", GetStatusAsync)
            .AllowAnonymous()
            .CacheOutput(CachePolicyName)
            .WithName("GetPublicStatus");

        group.MapGet("/a2s-history", GetA2sHistoryAsync)
            .AllowAnonymous()
            .CacheOutput(A2sHistoryCachePolicyName)
            .WithName("GetPublicA2sHistory");

        return group;
    }

    private static async Task<Ok<PublicStatusResponse>> GetStatusAsync(
        MonitoringReadService monitoring,
        CancellationToken cancellationToken)
    {
        var result = await monitoring.GetPublicStatusAsync(cancellationToken);
        return TypedResults.Ok(Map(result));
    }

    private static async Task<Results<Ok<PublicA2sHistoryResponse>, ValidationProblem>> GetA2sHistoryAsync(
        string? window,
        MonitoringReadService monitoring,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindow(window, out var parsedWindow))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["window"] = ["Window must be either '24h' or '7d'."]
            });
        }

        var result = await monitoring.GetPublicA2sHistoryAsync(parsedWindow, cancellationToken);
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

    private static PublicA2sHistoryResponse Map(PublicA2sHistoryDto history) =>
        new(
            MapWindow(history.Window),
            history.FromUtc,
            history.ToUtc,
            history.BucketMinutes,
            history.ObservedBuckets,
            history.TotalBuckets,
            history.ObservedReachabilityPercent,
            history.Buckets.Select(static bucket => new PublicA2sBucketResponse(
                bucket.StartedAtUtc,
                MapState(bucket.State),
                bucket.ObservedReachabilityPercent)).ToArray());

    private static string MapState(PublicStatusState state) => state switch
    {
        PublicStatusState.Operational => "operational",
        PublicStatusState.Degraded => "degraded",
        _ => "unknown"
    };

    private static string MapState(PublicA2sBucketState state) => state switch
    {
        PublicA2sBucketState.Operational => "operational",
        PublicA2sBucketState.Degraded => "degraded",
        PublicA2sBucketState.Unreachable => "unreachable",
        _ => "unknown"
    };

    private static bool TryParseWindow(string? value, out PublicA2sHistoryWindow window)
    {
        switch (value)
        {
            case null:
            case "24h":
                window = PublicA2sHistoryWindow.Last24Hours;
                return true;
            case "7d":
                window = PublicA2sHistoryWindow.Last7Days;
                return true;
            default:
                window = default;
                return false;
        }
    }

    private static string MapWindow(PublicA2sHistoryWindow window) => window switch
    {
        PublicA2sHistoryWindow.Last24Hours => "24h",
        PublicA2sHistoryWindow.Last7Days => "7d",
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unsupported A2S history window.")
    };
}
