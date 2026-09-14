namespace GoldSrcOps.Contracts.GameEvents;

public sealed record GameEventIngestRequest(
    short ContractVersion,
    Guid EventId,
    Guid SourceInstanceId,
    long SequenceNumber,
    string Type,
    DateTimeOffset OccurredAtUtc,
    string? Map,
    int? Players,
    int? Bots);
