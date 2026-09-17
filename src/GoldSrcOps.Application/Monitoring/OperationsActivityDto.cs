namespace GoldSrcOps.Application.Monitoring;

public sealed record OperationsActivityDto(
    int Limit,
    IReadOnlyList<OperationsActivityItemDto> Items,
    OperationsActivityPagePosition? PreviousPosition,
    OperationsActivityPagePosition? NextPosition);

public sealed record OperationsActivityPagePosition(
    int Offset,
    DateTimeOffset ToUtc);
