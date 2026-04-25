using GoldSrcOps.Application.Monitoring;
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

    public async Task<int> CountOpenIncidentsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.AvailabilityIncidents
            .AsNoTracking()
            .CountAsync(x => x.ClosedAtUtc == null, cancellationToken);
    }
}
