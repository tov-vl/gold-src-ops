namespace GoldSrcOps.Application.Monitoring;

public interface IMonitoringReadRepository
{
    Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PollSnapshotDto>> ListSnapshotsAsync(
        Guid serverId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DashboardServerStatusDto>> ListDashboardServerStatusesAsync(CancellationToken cancellationToken);

    Task<int> CountOpenIncidentsAsync(CancellationToken cancellationToken);
}
