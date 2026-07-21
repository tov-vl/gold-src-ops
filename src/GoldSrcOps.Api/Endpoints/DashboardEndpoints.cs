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

        return group;
    }

    private static async Task<Ok<DashboardOverviewResponse>> GetOverviewAsync(
        MonitoringReadService monitoring,
        CancellationToken cancellationToken)
    {
        var result = await monitoring.GetDashboardOverviewAsync(cancellationToken);
        return TypedResults.Ok(Map(result));
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
}
