namespace GoldSrcOps.Contracts.Alerts;

public sealed record PendingDeliveryListItemResponse(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    Guid? IncidentId,
    string IncidentStatus,
    Guid? ServerId,
    string? ServerName);
