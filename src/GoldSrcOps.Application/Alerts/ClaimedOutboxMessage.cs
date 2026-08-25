namespace GoldSrcOps.Application.Alerts;

public sealed record ClaimedOutboxMessage(
    Guid Id,
    string EventType,
    short PayloadVersion,
    string AggregateType,
    Guid AggregateId,
    DateTimeOffset OccurredAtUtc,
    string Payload,
    int AttemptCount,
    Guid ClaimId,
    DateTimeOffset ClaimedAtUtc);
