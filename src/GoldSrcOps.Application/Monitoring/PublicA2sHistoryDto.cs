namespace GoldSrcOps.Application.Monitoring;

public enum PublicA2sHistoryWindow
{
    Last24Hours,
    Last7Days
}

public enum PublicA2sBucketState
{
    Unknown,
    Operational,
    Degraded,
    Unreachable
}

public sealed record PublicA2sBucketCountDto(
    DateTimeOffset StartedAtUtc,
    int SampleCount,
    int ReachableSampleCount);

public sealed record PublicA2sBucketDto(
    DateTimeOffset StartedAtUtc,
    PublicA2sBucketState State,
    decimal? ObservedReachabilityPercent);

public sealed record PublicA2sHistoryDto(
    PublicA2sHistoryWindow Window,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int BucketMinutes,
    int ObservedBuckets,
    int TotalBuckets,
    decimal? ObservedReachabilityPercent,
    IReadOnlyList<PublicA2sBucketDto> Buckets);
