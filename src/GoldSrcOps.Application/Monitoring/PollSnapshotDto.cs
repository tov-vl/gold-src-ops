namespace GoldSrcOps.Application.Monitoring;

public sealed record PollSnapshotDto(
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
