namespace GoldSrcOps.Contracts.ProviderDelivery;

public sealed record ProviderDeliveryStatusResponse(
    bool IsEnabled,
    long PendingCount,
    long ProcessingCount,
    long DeadLetterCount,
    long UnreviewedDeadLetterCount,
    DateTimeOffset? OldestPendingAtUtc,
    DateTimeOffset ObservedAtUtc);
