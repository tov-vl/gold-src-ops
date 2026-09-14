using GoldSrcOps.Domain.GameEvents;

namespace GoldSrcOps.Application.GameEvents;

public sealed record IngestGameEventCommand(
    short ContractVersion,
    Guid EventId,
    Guid SourceInstanceId,
    long SequenceNumber,
    GameEventType Type,
    DateTimeOffset OccurredAtUtc,
    string? Map,
    int? Players,
    int? Bots);
