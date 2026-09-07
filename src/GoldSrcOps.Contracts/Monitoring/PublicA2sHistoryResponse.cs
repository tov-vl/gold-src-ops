namespace GoldSrcOps.Contracts.Monitoring;

public sealed record PublicA2sHistoryResponse(
    string Window,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int BucketMinutes,
    int ObservedBuckets,
    int TotalBuckets,
    decimal? ObservedReachabilityPercent,
    IReadOnlyList<PublicA2sBucketResponse> Buckets);

public sealed record PublicA2sBucketResponse(
    DateTimeOffset StartedAtUtc,
    string State,
    decimal? ObservedReachabilityPercent);
