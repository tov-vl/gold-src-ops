using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Monitoring;

public sealed class MonitoringReadService
{
    public const int DefaultSnapshotLimit = 100;
    public const int MaxSnapshotLimit = 500;

    private readonly IMonitoringReadRepository _repository;

    public MonitoringReadService(IMonitoringReadRepository repository)
    {
        _repository = repository;
    }

    public async Task<SnapshotHistoryDto?> ListSnapshotsAsync(
        Guid serverId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!await _repository.ServerExistsAsync(serverId, cancellationToken))
        {
            return null;
        }

        var effectiveLimit = Math.Clamp(limit ?? DefaultSnapshotLimit, 1, MaxSnapshotLimit);
        var snapshots = await _repository.ListSnapshotsAsync(
            serverId,
            fromUtc,
            toUtc,
            effectiveLimit,
            cancellationToken);

        return new SnapshotHistoryDto(serverId, fromUtc, toUtc, effectiveLimit, snapshots);
    }

    public async Task<DashboardOverviewDto> GetDashboardOverviewAsync(CancellationToken cancellationToken)
    {
        var servers = await _repository.ListDashboardServerStatusesAsync(cancellationToken);
        var openIncidents = await _repository.CountOpenIncidentsAsync(cancellationToken);

        var enabledServers = 0;
        var onlineServers = 0;
        var offlineServers = 0;
        var unknownServers = 0;
        DateTimeOffset? lastCheckedAtUtc = null;

        foreach (var server in servers)
        {
            if (server.IsEnabled)
            {
                enabledServers++;
            }

            switch (server.Status)
            {
                case ServerStatus.Online:
                    onlineServers++;
                    break;
                case ServerStatus.Offline:
                    offlineServers++;
                    break;
                default:
                    unknownServers++;
                    break;
            }

            if (server.LastCheckedAtUtc is not null &&
                (lastCheckedAtUtc is null || server.LastCheckedAtUtc > lastCheckedAtUtc))
            {
                lastCheckedAtUtc = server.LastCheckedAtUtc;
            }
        }

        return new DashboardOverviewDto(
            servers.Count,
            enabledServers,
            servers.Count - enabledServers,
            onlineServers,
            offlineServers,
            unknownServers,
            openIncidents,
            lastCheckedAtUtc);
    }
}
