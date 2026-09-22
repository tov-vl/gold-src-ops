using GoldSrcOps.Application.Common;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Monitoring;

public sealed class MonitoringReadService
{
    public const int DefaultSnapshotLimit = 100;
    public const int MaxSnapshotLimit = 500;
    public const int DefaultActivityLimit = 50;
    public const int MaxActivityLimit = 100;
    public const int MaxActivityOffset = 500;

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

    public async Task<FleetOverviewDto> GetFleetOverviewAsync(CancellationToken cancellationToken)
    {
        var states = await _repository.ListFleetServerStatesAsync(cancellationToken);
        var nowUtc = _clock.UtcNow;
        var servers = new FleetServerSummaryDto[states.Count];
        var enabledServers = 0;
        var onlineServers = 0;
        var offlineServers = 0;
        var unknownServers = 0;
        var openIncidents = 0;
        DateTimeOffset? lastCheckedAtUtc = null;

        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            var isStale = state.IsEnabled && IsStale(state, nowUtc);
            var requiresAttention = state.OpenIncidents > 0 ||
                (state.IsEnabled && (state.Status != ServerStatus.Online || isStale));

            servers[index] = new FleetServerSummaryDto(
                state.ServerId,
                state.Name,
                state.Game,
                state.Host,
                state.QueryPort,
                state.IsEnabled,
                state.PollIntervalSeconds,
                state.Status,
                state.LastCheckedAtUtc,
                state.LatencyMs,
                state.CurrentMap,
                state.Players,
                state.MaxPlayers,
                state.Bots,
                state.ConsecutiveFailures,
                state.OpenIncidents,
                isStale,
                requiresAttention);

            if (state.IsEnabled)
            {
                enabledServers++;
            }

            switch (state.Status)
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

            openIncidents += state.OpenIncidents;
            if (state.LastCheckedAtUtc is not null &&
                (lastCheckedAtUtc is null || state.LastCheckedAtUtc > lastCheckedAtUtc))
            {
                lastCheckedAtUtc = state.LastCheckedAtUtc;
            }
        }

        var overview = new DashboardOverviewDto(
            states.Count,
            enabledServers,
            states.Count - enabledServers,
            onlineServers,
            offlineServers,
            unknownServers,
            openIncidents,
            lastCheckedAtUtc);

        return new FleetOverviewDto(overview, servers);
    }

    public async Task<OperationsActivityDto> GetOperationsActivityAsync(
        int? limit,
        Guid? serverId,
        OperationsActivitySource? source,
        OperationsActivityWindow? window,
        OperationsActivityPagePosition? position,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit ?? DefaultActivityLimit, 1, MaxActivityLimit);
        var offset = position?.Offset ?? 0;
        if (offset is < 0 or > MaxActivityOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var toUtc = position?.ToUtc ?? TruncateToMicrosecondPrecision(_clock.UtcNow);
        DateTimeOffset? fromUtc = null;
        if (window is not null)
        {
            fromUtc = toUtc.Subtract(GetOperationsActivityWindowDuration(window.Value));
        }

        var items = await _repository.ListOperationsActivityAsync(
            offset,
            effectiveLimit + 1,
            serverId,
            source,
            fromUtc,
            toUtc,
            cancellationToken);

        var pageItems = items.Take(effectiveLimit).ToArray();
        var previousPosition = offset == 0
            ? null
            : new OperationsActivityPagePosition(Math.Max(0, offset - effectiveLimit), toUtc);
        var nextPosition = items.Count > effectiveLimit && offset + effectiveLimit <= MaxActivityOffset
            ? new OperationsActivityPagePosition(offset + effectiveLimit, toUtc)
            : null;

        return new OperationsActivityDto(
            effectiveLimit,
            pageItems,
            previousPosition,
            nextPosition);
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

    public async Task<PublicServerJoinDto?> GetPublicServerJoinAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var states = await _repository.ListFleetServerStatesAsync(cancellationToken);
        var server = states.SingleOrDefault(state => state.ServerId == serverId && state.IsEnabled);
        if (server is null)
        {
            return null;
        }

        var state = server.Status switch
        {
            ServerStatus.Online when !IsStale(server, _clock.UtcNow) => PublicServerJoinState.Online,
            ServerStatus.Offline => PublicServerJoinState.Offline,
            _ => PublicServerJoinState.Unknown
        };

        return new PublicServerJoinDto(
            state,
            server.CurrentMap,
            server.Players,
            server.MaxPlayers,
            server.LastCheckedAtUtc);
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

    public async Task<ServerTrendDto?> GetServerTrendAsync(
        Guid serverId,
        ServerTrendWindow window,
        CancellationToken cancellationToken)
    {
        if (!await _repository.ServerExistsAsync(serverId, cancellationToken))
        {
            return null;
        }

        var options = GetServerTrendWindowOptions(window);
        var toUtc = TruncateToMicrosecondPrecision(_clock.UtcNow);
        var fromUtc = toUtc.Subtract(options.Duration);
        var aggregates = await _repository.ListServerTrendBucketAggregatesAsync(
            serverId,
            fromUtc,
            toUtc,
            options.BucketSize,
            cancellationToken);
        var aggregatesByStart = aggregates.ToDictionary(static item => item.StartedAtUtc);
        var buckets = new ServerTrendBucketDto[options.TotalBuckets];
        var observedBuckets = 0;
        var sampleCount = 0;
        var reachableSampleCount = 0;
        var latencySampleCount = 0;
        long latencyTotalMilliseconds = 0;
        int? peakPlayers = null;
        int? peakBots = null;

        for (var index = 0; index < buckets.Length; index++)
        {
            var startedAtUtc = fromUtc.AddTicks(options.BucketSize.Ticks * index);
            if (!aggregatesByStart.TryGetValue(startedAtUtc, out var aggregate))
            {
                buckets[index] = new ServerTrendBucketDto(
                    startedAtUtc,
                    ServerTrendBucketState.Unknown,
                    SampleCount: 0,
                    ReachableSampleCount: 0,
                    ObservedReachabilityPercent: null,
                    AverageLatencyMs: null,
                    PeakPlayers: null,
                    PeakBots: null);
                continue;
            }

            ValidateServerTrendAggregate(aggregate, fromUtc, toUtc, options.BucketSize);
            observedBuckets++;
            sampleCount = checked(sampleCount + aggregate.SampleCount);
            reachableSampleCount = checked(reachableSampleCount + aggregate.ReachableSampleCount);
            latencySampleCount = checked(latencySampleCount + aggregate.LatencySampleCount);
            latencyTotalMilliseconds = checked(
                latencyTotalMilliseconds + aggregate.LatencyTotalMilliseconds);
            peakPlayers = MaxNullable(peakPlayers, aggregate.PeakPlayers);
            peakBots = MaxNullable(peakBots, aggregate.PeakBots);

            buckets[index] = new ServerTrendBucketDto(
                startedAtUtc,
                GetServerTrendBucketState(aggregate.ReachableSampleCount, aggregate.SampleCount),
                aggregate.SampleCount,
                aggregate.ReachableSampleCount,
                CalculateReachabilityPercent(aggregate.ReachableSampleCount, aggregate.SampleCount),
                CalculateAverageLatency(
                    aggregate.LatencyTotalMilliseconds,
                    aggregate.LatencySampleCount),
                aggregate.PeakPlayers,
                aggregate.PeakBots);
        }

        if (aggregatesByStart.Count != observedBuckets)
        {
            throw new InvalidOperationException("The server trend aggregate contains an unexpected bucket.");
        }

        return new ServerTrendDto(
            serverId,
            window,
            fromUtc,
            toUtc,
            (int)options.BucketSize.TotalMinutes,
            observedBuckets,
            options.TotalBuckets,
            sampleCount,
            reachableSampleCount,
            sampleCount == 0
                ? null
                : CalculateReachabilityPercent(reachableSampleCount, sampleCount),
            CalculateAverageLatency(latencyTotalMilliseconds, latencySampleCount),
            peakPlayers,
            peakBots,
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

    private static bool IsStale(FleetServerStateDto state, DateTimeOffset nowUtc)
    {
        if (state.LastCheckedAtUtc is null)
        {
            return true;
        }

        var freshnessWindow = TimeSpan.FromSeconds(
            checked((long)state.PollIntervalSeconds * 2 + 10));
        return nowUtc - state.LastCheckedAtUtc > freshnessWindow;
    }

    private static DateTimeOffset TruncateToMicrosecondPrecision(DateTimeOffset value)
    {
        var utcValue = value.ToUniversalTime();
        var truncatedTicks = utcValue.Ticks - utcValue.Ticks % TimeSpan.TicksPerMicrosecond;
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }

    private static TimeSpan GetOperationsActivityWindowDuration(OperationsActivityWindow window) => window switch
    {
        OperationsActivityWindow.LastHour => TimeSpan.FromHours(1),
        OperationsActivityWindow.Last6Hours => TimeSpan.FromHours(6),
        OperationsActivityWindow.Last24Hours => TimeSpan.FromHours(24),
        OperationsActivityWindow.Last7Days => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unsupported activity window.")
    };

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

    private static HistoryWindowOptions GetServerTrendWindowOptions(ServerTrendWindow window) => window switch
    {
        ServerTrendWindow.LastHour => new(
            Duration: TimeSpan.FromHours(1),
            BucketSize: TimeSpan.FromMinutes(5),
            TotalBuckets: 12),
        ServerTrendWindow.Last6Hours => new(
            Duration: TimeSpan.FromHours(6),
            BucketSize: TimeSpan.FromMinutes(15),
            TotalBuckets: 24),
        ServerTrendWindow.Last24Hours => new(
            Duration: TimeSpan.FromHours(24),
            BucketSize: TimeSpan.FromHours(1),
            TotalBuckets: 24),
        ServerTrendWindow.Last7Days => new(
            Duration: TimeSpan.FromDays(7),
            BucketSize: TimeSpan.FromHours(6),
            TotalBuckets: 28),
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unsupported server trend window.")
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

    private static void ValidateServerTrendAggregate(
        ServerTrendBucketAggregateDto aggregate,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan bucketSize)
    {
        var offset = aggregate.StartedAtUtc - fromUtc;
        if (aggregate.StartedAtUtc < fromUtc ||
            aggregate.StartedAtUtc >= toUtc ||
            offset.Ticks % bucketSize.Ticks != 0 ||
            aggregate.SampleCount <= 0 ||
            aggregate.ReachableSampleCount < 0 ||
            aggregate.ReachableSampleCount > aggregate.SampleCount ||
            aggregate.LatencySampleCount < 0 ||
            aggregate.LatencySampleCount > aggregate.ReachableSampleCount ||
            aggregate.LatencyTotalMilliseconds < 0 ||
            aggregate.PeakPlayers < 0 ||
            aggregate.PeakBots < 0)
        {
            throw new InvalidOperationException("The server trend aggregate contains invalid values.");
        }
    }

    private static ServerTrendBucketState GetServerTrendBucketState(
        int reachableSampleCount,
        int sampleCount)
    {
        if (reachableSampleCount == 0)
        {
            return ServerTrendBucketState.Unreachable;
        }

        return reachableSampleCount == sampleCount
            ? ServerTrendBucketState.Operational
            : ServerTrendBucketState.Degraded;
    }

    private static decimal? CalculateAverageLatency(long latencyTotalMilliseconds, int latencySampleCount) =>
        latencySampleCount == 0
            ? null
            : Math.Round(
                latencyTotalMilliseconds * 1m / latencySampleCount,
                decimals: 1,
                MidpointRounding.AwayFromZero);

    private static int? MaxNullable(int? current, int? candidate) => candidate is null
        ? current
        : current is null
            ? candidate
            : Math.Max(current.Value, candidate.Value);

    private static decimal CalculateReachabilityPercent(int reachableSampleCount, int sampleCount) =>
        Math.Round(
            reachableSampleCount * 100m / sampleCount,
            decimals: 1,
            MidpointRounding.AwayFromZero);

    private sealed record HistoryWindowOptions(TimeSpan Duration, TimeSpan BucketSize, int TotalBuckets);
}
