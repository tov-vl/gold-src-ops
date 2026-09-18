namespace GoldSrcOps.AlertReceiver.AvailabilityEvents;

internal sealed record AvailabilityEventRequest(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    Guid IncidentId,
    Guid ServerId,
    string ServerName,
    string Reason,
    int ConsecutiveFailures,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    long? DurationSeconds,
    short PayloadVersion);
