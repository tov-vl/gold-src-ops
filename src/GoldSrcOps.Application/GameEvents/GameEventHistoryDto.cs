using GoldSrcOps.Domain.GameEvents;

namespace GoldSrcOps.Application.GameEvents;

public sealed record GameEventHistoryDto(
    Guid ServerId,
    int Limit,
    IReadOnlyList<GameEventHistoryItemDto> Items);

public sealed record GameEventHistoryItemDto(
    GameEventType Type,
    DateTimeOffset OccurredAtUtc,
    string? Map,
    int? Players,
    int? Bots);
