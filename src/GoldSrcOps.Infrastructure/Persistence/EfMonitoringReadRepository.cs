using GoldSrcOps.Application.GameEvents;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.GameEvents;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfMonitoringReadRepository : IMonitoringReadRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfMonitoringReadRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .AnyAsync(x => x.Id == serverId, cancellationToken);
    }

    public async Task<IReadOnlyList<PollSnapshotDto>> ListSnapshotsAsync(
        Guid serverId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.PollSnapshots
            .AsNoTracking()
            .Where(x => x.ServerId == serverId);

        if (fromUtc is not null)
        {
            query = query.Where(x => x.CheckedAtUtc >= fromUtc);
        }

        if (toUtc is not null)
        {
            query = query.Where(x => x.CheckedAtUtc <= toUtc);
        }

        return await query
            .OrderByDescending(x => x.CheckedAtUtc)
            .Take(limit)
            .Select(x => new PollSnapshotDto(
                x.Id,
                x.ServerId,
                x.CheckedAtUtc,
                x.IsReachable,
                x.LatencyMs,
                x.Map,
                x.Players,
                x.MaxPlayers,
                x.Bots,
                x.RawVersion,
                x.FailureReason))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DashboardServerStatusDto>> ListDashboardServerStatusesAsync(
        CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .Select(x => new DashboardServerStatusDto(
                x.Id,
                x.IsEnabled,
                x.CurrentState == null ? ServerStatus.Unknown : x.CurrentState.Status,
                x.CurrentState == null ? null : x.CurrentState.LastCheckedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetServerStateDto>> ListFleetServerStatesAsync(
        CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Select(x => new FleetServerStateDto(
                x.Id,
                x.Name,
                x.Game,
                x.Endpoint.Host,
                x.Endpoint.QueryPort,
                x.IsEnabled,
                x.PollIntervalSeconds,
                x.CurrentState == null ? ServerStatus.Unknown : x.CurrentState.Status,
                x.CurrentState == null ? null : x.CurrentState.LastCheckedAtUtc,
                x.CurrentState == null ? null : x.CurrentState.LatencyMs,
                x.CurrentState == null ? null : x.CurrentState.CurrentMap,
                x.CurrentState == null ? null : x.CurrentState.Players,
                x.CurrentState == null ? null : x.CurrentState.MaxPlayers,
                _dbContext.PollSnapshots
                    .Where(snapshot => snapshot.ServerId == x.Id)
                    .OrderByDescending(snapshot => snapshot.CheckedAtUtc)
                    .ThenByDescending(snapshot => snapshot.Id)
                    .Select(snapshot => snapshot.Bots)
                    .FirstOrDefault(),
                x.CurrentState == null ? 0 : x.CurrentState.ConsecutiveFailures,
                _dbContext.AvailabilityIncidents.Count(incident =>
                    incident.ServerId == x.Id && incident.ClosedAtUtc == null)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationsActivityItemDto>> ListOperationsActivityAsync(
        int limit,
        Guid? serverId,
        OperationsActivitySource? source,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IncidentActivityRow> incidents = [];
        if (source is null or OperationsActivitySource.Incident)
        {
            var query = _dbContext.AvailabilityIncidents.AsNoTracking();
            if (serverId is not null)
            {
                query = query.Where(x => x.ServerId == serverId.Value);
            }

            incidents = await query
                .OrderByDescending(x => x.ClosedAtUtc ?? x.OpenedAtUtc)
                .ThenByDescending(x => x.Id)
                .Take(limit)
                .Select(x => new IncidentActivityRow(
                    x.Id,
                    x.ServerId,
                    x.Server.Name,
                    x.Type,
                    x.OpenedAtUtc,
                    x.ClosedAtUtc))
                .ToListAsync(cancellationToken);
        }

        IReadOnlyList<CommandActivityRow> commands = [];
        if (source is null or OperationsActivitySource.Command)
        {
            var query = _dbContext.CommandExecutions.AsNoTracking();
            if (serverId is not null)
            {
                query = query.Where(x => x.ServerId == serverId.Value);
            }

            commands = await query
                .OrderByDescending(x => x.CompletedAtUtc ?? x.StartedAtUtc ?? x.RequestedAtUtc)
                .ThenByDescending(x => x.Id)
                .Take(limit)
                .Select(x => new CommandActivityRow(
                    x.Id,
                    x.ServerId,
                    x.Server.Name,
                    x.Type,
                    x.Status,
                    x.RequestedAtUtc,
                    x.StartedAtUtc,
                    x.CompletedAtUtc))
                .ToListAsync(cancellationToken);
        }

        IReadOnlyList<GameplayActivityRow> gameplayEvents = [];
        if (source is null or OperationsActivitySource.Gameplay)
        {
            var query = _dbContext.GameEventInbox
                .AsNoTracking()
                .Where(x => x.Type == GameEventType.RoundEnded);
            if (serverId is not null)
            {
                query = query.Where(x => x.ServerId == serverId.Value);
            }

            gameplayEvents = await query
                .OrderByDescending(x => x.OccurredAtUtc)
                .ThenByDescending(x => x.Id)
                .Take(limit)
                .Select(x => new GameplayActivityRow(
                    x.Id,
                    x.ServerId,
                    x.Server.Name,
                    x.Type,
                    x.OccurredAtUtc))
                .ToListAsync(cancellationToken);
        }

        return incidents
            .Select(static incident => new OperationsActivityItemDto(
                incident.SourceId,
                "Incident",
                incident.ServerId,
                incident.ServerName,
                incident.Type.ToString(),
                incident.ClosedAtUtc is null ? "Open" : "Recovered",
                incident.ClosedAtUtc ?? incident.OpenedAtUtc))
            .Concat(commands.Select(static command => new OperationsActivityItemDto(
                command.SourceId,
                "Command",
                command.ServerId,
                command.ServerName,
                command.Type.ToString(),
                command.Status.ToString(),
                command.CompletedAtUtc ?? command.StartedAtUtc ?? command.RequestedAtUtc)))
            .Concat(gameplayEvents.Select(static gameEvent => new OperationsActivityItemDto(
                gameEvent.SourceId,
                "Gameplay",
                gameEvent.ServerId,
                gameEvent.ServerName,
                GameEventTypeContract.ToWireValue(gameEvent.Type),
                "Recorded",
                gameEvent.OccurredAtUtc)))
            .OrderByDescending(static item => item.OccurredAtUtc)
            .ThenBy(static item => item.SourceType, StringComparer.Ordinal)
            .ThenBy(static item => item.SourceId)
            .Take(limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<PublicA2sBucketCountDto>> ListPublicA2sBucketCountsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan bucketSize,
        CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Database
            .SqlQuery<PublicA2sBucketCountRow>($"""
                SELECT
                    date_bin({bucketSize}, p."CheckedAtUtc", {fromUtc}) AS "StartedAtUtc",
                    COUNT(*)::integer AS "SampleCount",
                    COUNT(*) FILTER (WHERE p."IsReachable")::integer AS "ReachableSampleCount"
                FROM goldsrcops.poll_snapshots AS p
                INNER JOIN goldsrcops.servers AS s ON s."Id" = p."ServerId"
                WHERE s."IsEnabled"
                  AND p."CheckedAtUtc" >= {fromUtc}
                  AND p."CheckedAtUtc" < {toUtc}
                GROUP BY 1
                ORDER BY 1
                """)
            .ToListAsync(cancellationToken);

        return rows
            .Select(static row => new PublicA2sBucketCountDto(
                row.StartedAtUtc,
                row.SampleCount,
                row.ReachableSampleCount))
            .ToArray();
    }

    public async Task<IReadOnlyList<ServerTrendBucketAggregateDto>> ListServerTrendBucketAggregatesAsync(
        Guid serverId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan bucketSize,
        CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Database
            .SqlQuery<ServerTrendBucketAggregateRow>($"""
                SELECT
                    date_bin({bucketSize}, p."CheckedAtUtc", {fromUtc}) AS "StartedAtUtc",
                    COUNT(*)::integer AS "SampleCount",
                    COUNT(*) FILTER (WHERE p."IsReachable")::integer AS "ReachableSampleCount",
                    COUNT(p."LatencyMs") FILTER (WHERE p."IsReachable")::integer AS "LatencySampleCount",
                    COALESCE(SUM(p."LatencyMs") FILTER (WHERE p."IsReachable"), 0)::bigint
                        AS "LatencyTotalMilliseconds",
                    MAX(p."Players") FILTER (WHERE p."IsReachable") AS "PeakPlayers",
                    MAX(p."Bots") FILTER (WHERE p."IsReachable") AS "PeakBots"
                FROM goldsrcops.poll_snapshots AS p
                WHERE p."ServerId" = {serverId}
                  AND p."CheckedAtUtc" >= {fromUtc}
                  AND p."CheckedAtUtc" < {toUtc}
                GROUP BY 1
                ORDER BY 1
                """)
            .ToListAsync(cancellationToken);

        return rows
            .Select(static row => new ServerTrendBucketAggregateDto(
                row.StartedAtUtc,
                row.SampleCount,
                row.ReachableSampleCount,
                row.LatencySampleCount,
                row.LatencyTotalMilliseconds,
                row.PeakPlayers,
                row.PeakBots))
            .ToArray();
    }

    public async Task<int> CountOpenIncidentsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.AvailabilityIncidents
            .AsNoTracking()
            .CountAsync(x => x.ClosedAtUtc == null, cancellationToken);
    }

    public async Task<int> CountOpenIncidentsForEnabledServersAsync(CancellationToken cancellationToken)
    {
        return await (
            from incident in _dbContext.AvailabilityIncidents.AsNoTracking()
            join server in _dbContext.Servers.AsNoTracking() on incident.ServerId equals server.Id
            where incident.ClosedAtUtc == null && server.IsEnabled
            select incident.Id)
            .CountAsync(cancellationToken);
    }

    private sealed class PublicA2sBucketCountRow
    {
        public DateTimeOffset StartedAtUtc { get; init; }

        public int SampleCount { get; init; }

        public int ReachableSampleCount { get; init; }
    }

    private sealed record IncidentActivityRow(
        Guid SourceId,
        Guid ServerId,
        string ServerName,
        IncidentType Type,
        DateTimeOffset OpenedAtUtc,
        DateTimeOffset? ClosedAtUtc);

    private sealed record CommandActivityRow(
        Guid SourceId,
        Guid ServerId,
        string ServerName,
        ServerCommandType Type,
        CommandExecutionStatus Status,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc);

    private sealed record GameplayActivityRow(
        Guid SourceId,
        Guid ServerId,
        string ServerName,
        GameEventType Type,
        DateTimeOffset OccurredAtUtc);

    private sealed class ServerTrendBucketAggregateRow
    {
        public DateTimeOffset StartedAtUtc { get; init; }

        public int SampleCount { get; init; }

        public int ReachableSampleCount { get; init; }

        public int LatencySampleCount { get; init; }

        public long LatencyTotalMilliseconds { get; init; }

        public int? PeakPlayers { get; init; }

        public int? PeakBots { get; init; }
    }
}
