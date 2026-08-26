namespace GoldSrcOps.Contracts.Alerts;

public sealed record DeadLetterListItemResponse(
    Guid EventId,
    string EventType,
    short PayloadVersion,
    string AggregateType,
    Guid AggregateId,
    DateTimeOffset OccurredAtUtc,
    int AttemptCount,
    int ReplayCount,
    DateTimeOffset? DeadLetteredAtUtc,
    string? LastError);
