namespace GoldSrcOps.Application.Alerts;

public sealed record DeadLetterPagePosition(
    DateTimeOffset? DeadLetteredAtUtc,
    DateTimeOffset OccurredAtUtc,
    Guid EventId);
