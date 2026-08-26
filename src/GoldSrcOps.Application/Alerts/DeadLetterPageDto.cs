namespace GoldSrcOps.Application.Alerts;

public sealed record DeadLetterPageDto(
    int Limit,
    IReadOnlyList<DeadLetterListItemDto> Items,
    DeadLetterPagePosition? NextPosition);
