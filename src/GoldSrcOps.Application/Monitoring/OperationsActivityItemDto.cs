namespace GoldSrcOps.Application.Monitoring;

public sealed record OperationsActivityItemDto(
    Guid SourceId,
    string SourceType,
    Guid ServerId,
    string ServerName,
    string Category,
    string State,
    DateTimeOffset OccurredAtUtc);
