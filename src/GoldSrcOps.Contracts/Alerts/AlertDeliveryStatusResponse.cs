namespace GoldSrcOps.Contracts.Alerts;

public sealed record AlertDeliveryStatusResponse(
    bool IsEnabled,
    long PendingCount,
    long ProcessingCount,
    long DeadLetterCount,
    DateTimeOffset? OldestPendingAtUtc,
    DateTimeOffset ObservedAtUtc);
