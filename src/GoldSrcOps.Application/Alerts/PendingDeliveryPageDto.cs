namespace GoldSrcOps.Application.Alerts;

public sealed record PendingDeliveryPageDto(
    int Limit,
    IReadOnlyList<PendingDeliveryListItemDto> Items,
    PendingDeliveryPagePosition? NextPosition);
