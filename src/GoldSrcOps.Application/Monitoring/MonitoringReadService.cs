using GoldSrcOps.Application.Common;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Monitoring;

public sealed class MonitoringReadService
{
    public const int DefaultSnapshotLimit = 100;
    public const int MaxSnapshotLimit = 500;

    private readonly IMonitoringReadRepository _repository;
    private readonly IClock _clock;

    public MonitoringReadService(IMonitoringReadRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
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

    public async Task<PublicA2sHistoryDto> GetPublicA2sHistoryAsync(
        PublicA2sHistoryWindow window,
        CancellationToken cancellationToken)
    {
        var options = GetHistoryWindowOptions(window);
        var toUtc = TruncateToMicrosecondPrecision(_clock.UtcNow);
        var fromUtc = toUtc.Subtract(options.Duration);
        var counts = await _repository.ListPublicA2sBucketCountsAsync(
            fromUtc,
            toUtc,
            options.BucketSize,
            cancellationToken);
        var countsByStart = counts.ToDictionary(static item => item.StartedAtUtc);
        var buckets = new PublicA2sBucketDto[options.TotalBuckets];
        var observedBuckets = 0;
        var sampleCount = 0;
        var reachableSampleCount = 0;

        for (var index = 0; index < buckets.Length; index++)
        {
            var startedAtUtc = fromUtc.AddTicks(options.BucketSize.Ticks * index);
            if (!countsByStart.TryGetValue(startedAtUtc, out var count))
            {
                buckets[index] = new PublicA2sBucketDto(
                    startedAtUtc,
                    PublicA2sBucketState.Unknown,
                    ObservedReachabilityPercent: null);
                continue;
            }

            ValidateBucketCount(count);
            observedBuckets++;
            sampleCount += count.SampleCount;
            reachableSampleCount += count.ReachableSampleCount;
            var percentage = CalculateReachabilityPercent(count.ReachableSampleCount, count.SampleCount);
            buckets[index] = new PublicA2sBucketDto(
                startedAtUtc,
                GetBucketState(count.ReachableSampleCount, count.SampleCount),
                percentage);
        }

        return new PublicA2sHistoryDto(
            window,
            fromUtc,
            toUtc,
            (int)options.BucketSize.TotalMinutes,
            observedBuckets,
            options.TotalBuckets,
            sampleCount == 0
                ? null
                : CalculateReachabilityPercent(reachableSampleCount, sampleCount),
            buckets);
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

    private static DateTimeOffset TruncateToMicrosecondPrecision(DateTimeOffset value)
    {
        var utcValue = value.ToUniversalTime();
        var truncatedTicks = utcValue.Ticks - utcValue.Ticks % TimeSpan.TicksPerMicrosecond;
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }

    private static HistoryWindowOptions GetHistoryWindowOptions(PublicA2sHistoryWindow window) => window switch
    {
        PublicA2sHistoryWindow.Last24Hours => new(
            Duration: TimeSpan.FromHours(24),
            BucketSize: TimeSpan.FromHours(1),
            TotalBuckets: 24),
        PublicA2sHistoryWindow.Last7Days => new(
            Duration: TimeSpan.FromDays(7),
            BucketSize: TimeSpan.FromHours(6),
            TotalBuckets: 28),
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unsupported A2S history window.")
    };

    private static void ValidateBucketCount(PublicA2sBucketCountDto count)
    {
        if (count.SampleCount <= 0 ||
            count.ReachableSampleCount < 0 ||
            count.ReachableSampleCount > count.SampleCount)
        {
            throw new InvalidOperationException("The A2S history aggregate contains invalid sample counts.");
        }
    }

    private static PublicA2sBucketState GetBucketState(int reachableSampleCount, int sampleCount)
    {
        if (reachableSampleCount == 0)
        {
            return PublicA2sBucketState.Unreachable;
        }

        return reachableSampleCount == sampleCount
            ? PublicA2sBucketState.Operational
            : PublicA2sBucketState.Degraded;
    }

    private static decimal CalculateReachabilityPercent(int reachableSampleCount, int sampleCount) =>
        Math.Round(
            reachableSampleCount * 100m / sampleCount,
            decimals: 1,
            MidpointRounding.AwayFromZero);

    private sealed record HistoryWindowOptions(TimeSpan Duration, TimeSpan BucketSize, int TotalBuckets);
}
