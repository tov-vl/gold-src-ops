namespace GoldSrcOps.Application.Alerts;

public sealed record DeadLetterReplayCommand(
    Guid RequestId,
    Guid EventId,
    string RequestedBy,
    string Reason);
