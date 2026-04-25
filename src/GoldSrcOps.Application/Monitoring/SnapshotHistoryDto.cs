namespace GoldSrcOps.Application.Monitoring;

public sealed record SnapshotHistoryDto(
    Guid ServerId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Limit,
    IReadOnlyList<PollSnapshotDto> Items);
