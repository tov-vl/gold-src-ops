namespace GoldSrcOps.Application.Alerts;

public sealed record AlertDeliveryQueueStatistics(
    long PendingCount,
    long ProcessingCount,
    long DeadLetterCount,
    DateTimeOffset? OldestPendingAtUtc);
