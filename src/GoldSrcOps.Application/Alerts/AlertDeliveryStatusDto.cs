namespace GoldSrcOps.Application.Alerts;

public sealed record AlertDeliveryStatusDto(
    bool IsEnabled,
    long PendingCount,
    long ProcessingCount,
    long DeadLetterCount,
    DateTimeOffset? OldestPendingAtUtc,
    DateTimeOffset ObservedAtUtc);
