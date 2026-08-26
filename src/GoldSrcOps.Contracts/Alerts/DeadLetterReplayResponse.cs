namespace GoldSrcOps.Contracts.Alerts;

public sealed record DeadLetterReplayResponse(
    Guid RequestId,
    Guid EventId,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc,
    string Reason,
    int ReplayNumber,
    int PreviousAttemptCount,
    DateTimeOffset? PreviousDeadLetteredAtUtc,
    string Status,
    DateTimeOffset NextAttemptAtUtc);
