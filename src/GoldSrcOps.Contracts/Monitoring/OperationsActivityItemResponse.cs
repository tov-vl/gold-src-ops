namespace GoldSrcOps.Contracts.Monitoring;

public sealed record OperationsActivityItemResponse(
    Guid SourceId,
    string SourceType,
    Guid ServerId,
    string ServerName,
    string Category,
    string State,
    DateTimeOffset OccurredAtUtc);
