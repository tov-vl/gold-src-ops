namespace GoldSrcOps.Application.Alerts;

public sealed record DeadLetterDetailsDto(
    Guid EventId,
    string EventType,
    short PayloadVersion,
    string AggregateType,
    Guid AggregateId,
    DateTimeOffset OccurredAtUtc,
    string Payload,
    int AttemptCount,
    int ReplayCount,
    DateTimeOffset? DeadLetteredAtUtc,
    string? LastError,
    NewerOutboxMessageDto? NewerEvent);
