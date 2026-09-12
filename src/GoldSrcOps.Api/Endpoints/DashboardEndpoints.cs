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
}
