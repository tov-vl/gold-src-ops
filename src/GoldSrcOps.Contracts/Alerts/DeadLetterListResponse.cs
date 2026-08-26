namespace GoldSrcOps.Contracts.Alerts;

public sealed record DeadLetterListResponse(
    int Limit,
    string? NextCursor,
    IReadOnlyList<DeadLetterListItemResponse> Items);
