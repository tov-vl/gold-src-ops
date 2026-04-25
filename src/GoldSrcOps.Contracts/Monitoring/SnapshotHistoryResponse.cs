namespace GoldSrcOps.Contracts.Monitoring;

public sealed record SnapshotHistoryResponse(
    Guid ServerId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Limit,
    IReadOnlyList<PollSnapshotResponse> Items);
