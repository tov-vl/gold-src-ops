namespace GoldSrcOps.Contracts.Monitoring;

public sealed record ServerTrendResponse(
    Guid ServerId,
    string Window,
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
    IReadOnlyList<ServerTrendBucketResponse> Buckets);

public sealed record ServerTrendBucketResponse(
    DateTimeOffset StartedAtUtc,
    string State,
    int SampleCount,
    int ReachableSampleCount,
    decimal? ObservedReachabilityPercent,
    decimal? AverageLatencyMs,
    int? PeakPlayers,
    int? PeakBots);
