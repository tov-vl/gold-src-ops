using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Monitoring;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GoldSrcOps.Api.Endpoints;

public static class DashboardEndpoints
{
    public static RouteGroupBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/dashboard")
            .WithTags("Dashboard")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        group.MapGet("/overview", GetOverviewAsync)
            .WithName("GetDashboardOverview");

        group.MapGet("/fleet", GetFleetAsync)
            .WithName("GetDashboardFleet");

        group.MapGet("/activity", GetActivityAsync)
            .WithName("GetDashboardActivity");

        return group;
    }

    private static async Task<Ok<DashboardOverviewResponse>> GetOverviewAsync(
        MonitoringReadService monitoring,
        CancellationToken cancellationToken)
    {
        var result = await monitoring.GetDashboardOverviewAsync(cancellationToken);
        return TypedResults.Ok(Map(result));
    }

    private static async Task<Ok<FleetOverviewResponse>> GetFleetAsync(
        MonitoringReadService monitoring,
        CancellationToken cancellationToken)
    {
        var result = await monitoring.GetFleetOverviewAsync(cancellationToken);
        return TypedResults.Ok(new FleetOverviewResponse(
            Map(result.Overview),
            result.Servers.Select(Map).ToArray()));
    }

    private static async Task<Results<Ok<OperationsActivityResponse>, ValidationProblem>> GetActivityAsync(
        int? limit,
        Guid? serverId,
        string? kind,
        MonitoringReadService monitoring,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MonitoringReadService.MaxActivityLimit)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["limit"] =
                    [$"Limit must be between 1 and {MonitoringReadService.MaxActivityLimit}."]
            });
        }

        if (!TryParseActivitySource(kind, out var source))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] = ["Kind must be one of: all, incidents, commands, gameplay."]
            });
        }

        var result = await monitoring.GetOperationsActivityAsync(
            limit,
            serverId,
            source,
            cancellationToken);
        return TypedResults.Ok(new OperationsActivityResponse(
            result.Limit,
            result.Items.Select(Map).ToArray()));
    }

    private static bool TryParseActivitySource(
        string? kind,
        out OperationsActivitySource? source)
    {
        if (string.IsNullOrWhiteSpace(kind) ||
            string.Equals(kind.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            source = null;
            return true;
        }

        source = kind.Trim().ToLowerInvariant() switch
        {
            "incidents" => OperationsActivitySource.Incident,
            "commands" => OperationsActivitySource.Command,
            "gameplay" => OperationsActivitySource.Gameplay,
            _ => null
        };

        return source is not null;
    }

    private static DashboardOverviewResponse Map(DashboardOverviewDto overview) =>
        new(
            overview.TotalServers,
            overview.EnabledServers,
            overview.DisabledServers,
            overview.OnlineServers,
            overview.OfflineServers,
            overview.UnknownServers,
            overview.OpenIncidents,
            overview.LastCheckedAtUtc);

    private static FleetServerSummaryResponse Map(FleetServerSummaryDto server) =>
        new(
            server.ServerId,
            server.Name,
            server.Game.ToString(),
            server.Host,
            server.QueryPort,
            server.IsEnabled,
            server.PollIntervalSeconds,
            server.Status.ToString(),
            server.LastCheckedAtUtc,
            server.LatencyMs,
            server.CurrentMap,
            server.Players,
            server.MaxPlayers,
            server.Bots,
            server.ConsecutiveFailures,
            server.OpenIncidents,
            server.IsStale,
            server.RequiresAttention);

    private static OperationsActivityItemResponse Map(OperationsActivityItemDto item) =>
        new(
            item.SourceId,
            item.SourceType,
            item.ServerId,
            item.ServerName,
            item.Category,
            item.State,
            item.OccurredAtUtc);
}
