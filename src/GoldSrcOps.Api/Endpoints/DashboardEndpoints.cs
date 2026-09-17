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
        string? window,
        string? cursor,
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

        if (!TryParseActivityWindow(window, out var activityWindow))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["window"] = ["Window must be one of: 1h, 6h, 24h, 7d."]
            });
        }

        var effectiveLimit = limit ?? MonitoringReadService.DefaultActivityLimit;
        var cursorScope = new OperationsActivityCursorScope(
            effectiveLimit,
            serverId,
            source,
            activityWindow);
        OperationsActivityPagePosition? position = null;
        if (cursor is not null &&
            !OperationsActivityCursor.TryDecode(cursor, cursorScope, out position))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["cursor"] = ["Cursor is invalid, belongs to different filters, or is no longer supported."]
            });
        }

        var result = await monitoring.GetOperationsActivityAsync(
            limit,
            serverId,
            source,
            activityWindow,
            position,
            cancellationToken);
        return TypedResults.Ok(new OperationsActivityResponse(
            result.Limit,
            result.Items.Select(Map).ToArray(),
            result.PreviousPosition is null
                ? null
                : OperationsActivityCursor.Encode(result.PreviousPosition, cursorScope),
            result.NextPosition is null
                ? null
                : OperationsActivityCursor.Encode(result.NextPosition, cursorScope)));
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

    private static bool TryParseActivityWindow(
        string? value,
        out OperationsActivityWindow? window)
    {
        window = value switch
        {
            null => null,
            "1h" => OperationsActivityWindow.LastHour,
            "6h" => OperationsActivityWindow.Last6Hours,
            "24h" => OperationsActivityWindow.Last24Hours,
            "7d" => OperationsActivityWindow.Last7Days,
            _ => null
        };

        return value is null || window is not null;
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
