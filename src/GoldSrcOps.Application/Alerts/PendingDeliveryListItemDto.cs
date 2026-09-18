namespace GoldSrcOps.Application.Alerts;

public sealed record PendingDeliveryListItemDto(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    Guid? IncidentId,
    string IncidentStatus,
    Guid? ServerId,
    string? ServerName);
