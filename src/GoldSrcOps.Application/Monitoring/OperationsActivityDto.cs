namespace GoldSrcOps.Application.Monitoring;

public sealed record OperationsActivityDto(
    int Limit,
    IReadOnlyList<OperationsActivityItemDto> Items);
