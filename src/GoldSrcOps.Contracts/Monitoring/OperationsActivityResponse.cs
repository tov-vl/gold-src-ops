namespace GoldSrcOps.Contracts.Monitoring;

public sealed record OperationsActivityResponse(
    int Limit,
    IReadOnlyList<OperationsActivityItemResponse> Items);
