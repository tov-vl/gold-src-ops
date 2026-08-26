namespace GoldSrcOps.Application.Alerts;

public sealed record DeadLetterReplayRecordDto(
    Guid RequestId,
    Guid EventId,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc,
    string Reason,
    int ReplayNumber,
    int PreviousAttemptCount,
    DateTimeOffset? PreviousDeadLetteredAtUtc,
    DateTimeOffset NextAttemptAtUtc);
