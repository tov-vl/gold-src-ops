namespace GoldSrcOps.Contracts.Monitoring;

public sealed record PollSnapshotResponse(
    Guid Id,
    Guid ServerId,
    DateTimeOffset CheckedAtUtc,
    bool IsReachable,
    int? LatencyMs,
    string? Map,
    int? Players,
    int? MaxPlayers,
    int? Bots,
    string? RawVersion,
    string? FailureReason);
