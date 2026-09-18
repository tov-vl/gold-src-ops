namespace GoldSrcOps.Contracts.Alerts;

public sealed record PendingDeliveryListResponse(
    int Limit,
    string? NextCursor,
    IReadOnlyList<PendingDeliveryListItemResponse> Items);
