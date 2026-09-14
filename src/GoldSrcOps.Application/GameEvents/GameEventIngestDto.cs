using GoldSrcOps.Domain.GameEvents;

namespace GoldSrcOps.Application.GameEvents;

public sealed record GameEventIngestDto(
    Guid EventId,
    Guid ServerId,
    Guid SourceInstanceId,
    long SequenceNumber,
    short ContractVersion,
    GameEventType Type,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ReceivedAtUtc,
    bool Duplicate);
