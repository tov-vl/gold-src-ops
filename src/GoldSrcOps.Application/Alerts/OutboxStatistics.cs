namespace GoldSrcOps.Application.Alerts;

public sealed record OutboxStatistics(
    long PendingCount,
    DateTimeOffset? OldestPendingAtUtc,
    long DeadLetterCount);
