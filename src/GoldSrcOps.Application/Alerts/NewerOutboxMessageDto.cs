namespace GoldSrcOps.Application.Alerts;

public sealed record NewerOutboxMessageDto(
    Guid EventId,
    string Status,
    DateTimeOffset OccurredAtUtc);
