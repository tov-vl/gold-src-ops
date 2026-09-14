namespace GoldSrcOps.Contracts.GameEvents;

public sealed record GameEventIngestResponse(
    Guid EventId,
    Guid ServerId,
    Guid SourceInstanceId,
    long SequenceNumber,
    short ContractVersion,
    string Type,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ReceivedAtUtc,
    bool Duplicate);
