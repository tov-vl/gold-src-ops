namespace GoldSrcOps.Application.Alerts;

public sealed record DeadLetterListItemDto(
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
