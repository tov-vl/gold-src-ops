namespace GoldSrcOps.Contracts.Servers;

public sealed record ServerStatusResponse(
    Guid ServerId,
    string Status,
    bool IsReachable,
    DateTimeOffset LastCheckedAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    int? LatencyMs,
    string? CurrentMap,
    int? Players,
    int? MaxPlayers,
    string? FailureReason);
