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

    public async Task<PublicStatusDto> GetPublicStatusAsync(CancellationToken cancellationToken)
    {
        var servers = await _repository.ListDashboardServerStatusesAsync(cancellationToken);
        var openIncidents = await _repository.CountOpenIncidentsForEnabledServersAsync(cancellationToken);

        var monitoredServers = 0;
        var onlineServers = 0;
        var serversRequiringAttention = 0;
        DateTimeOffset? lastObservedAtUtc = null;

        foreach (var server in servers)
        {
            if (!server.IsEnabled)
            {
                continue;
            }

            monitoredServers++;

            if (server.Status == ServerStatus.Online)
            {
                onlineServers++;
            }
            else
            {
                serversRequiringAttention++;
            }

            if (server.LastCheckedAtUtc is not null &&
                (lastObservedAtUtc is null || server.LastCheckedAtUtc > lastObservedAtUtc))
            {
                lastObservedAtUtc = server.LastCheckedAtUtc;
            }
        }

        var state = GetPublicStatusState(
            monitoredServers,
            serversRequiringAttention,
            openIncidents,
            lastObservedAtUtc);

        return new PublicStatusDto(
            state,
            monitoredServers,
            onlineServers,
            serversRequiringAttention,
            openIncidents,
            lastObservedAtUtc);
    }

    private static PublicStatusState GetPublicStatusState(
        int monitoredServers,
        int serversRequiringAttention,
        int openIncidents,
        DateTimeOffset? lastObservedAtUtc)
    {
        if (monitoredServers == 0 || lastObservedAtUtc is null)
        {
            return PublicStatusState.Unknown;
        }

        return serversRequiringAttention > 0 || openIncidents > 0
            ? PublicStatusState.Degraded
            : PublicStatusState.Operational;
    }
}
