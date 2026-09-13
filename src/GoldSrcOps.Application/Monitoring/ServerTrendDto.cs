namespace GoldSrcOps.Application.Monitoring;

public enum ServerTrendWindow
{
    LastHour,
    Last6Hours,
    Last24Hours,
    Last7Days
}

public enum ServerTrendBucketState
{
    Unknown,
    Operational,
    Degraded,
    Unreachable
}

public sealed record ServerTrendBucketAggregateDto(
    DateTimeOffset StartedAtUtc,
    int SampleCount,
    int ReachableSampleCount,
    int LatencySampleCount,
    long LatencyTotalMilliseconds,
    int? PeakPlayers,
    int? PeakBots);

public sealed record ServerTrendBucketDto(
    DateTimeOffset StartedAtUtc,
    ServerTrendBucketState State,
    int SampleCount,
    int ReachableSampleCount,
    decimal? ObservedReachabilityPercent,
    decimal? AverageLatencyMs,
    int? PeakPlayers,
    int? PeakBots);

public sealed record ServerTrendDto(
    Guid ServerId,
    ServerTrendWindow Window,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int BucketMinutes,
    int ObservedBuckets,
    int TotalBuckets,
    int SampleCount,
    int ReachableSampleCount,
    decimal? ObservedReachabilityPercent,
    decimal? AverageLatencyMs,
    int? PeakPlayers,
    int? PeakBots,
    IReadOnlyList<ServerTrendBucketDto> Buckets);
