namespace GoldSrcOps.Contracts.GameEvents;

public sealed record GameEventHistoryResponse(
    Guid ServerId,
    int Limit,
    IReadOnlyList<GameEventHistoryItemResponse> Items);

public sealed record GameEventHistoryItemResponse(
    string Type,
    DateTimeOffset OccurredAtUtc,
    string? Map,
    int? Players,
    int? Bots);
