namespace GoldSrcOps.Application.Availability;

public sealed record ProviderProbeSample(
    DateTimeOffset SourceSampleTimestampUtc,
    bool Succeeded,
    int? HttpStatus,
    TimeSpan? Duration,
    ProbeFailureKind? FailureKind = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null);
