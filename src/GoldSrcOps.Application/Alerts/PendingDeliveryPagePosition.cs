namespace GoldSrcOps.Application.Alerts;

public sealed record PendingDeliveryPagePosition(
    DateTimeOffset NextAttemptAtUtc,
    DateTimeOffset OccurredAtUtc,
    Guid EventId);
