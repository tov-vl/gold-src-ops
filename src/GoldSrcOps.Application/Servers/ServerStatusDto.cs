using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Servers;

public sealed record ServerStatusDto(
    Guid ServerId,
    ServerStatus Status,
    bool IsReachable,
    DateTimeOffset LastCheckedAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    int? LatencyMs,
    string? CurrentMap,
    int? Players,
    int? MaxPlayers,
    string? FailureReason,
    int ConsecutiveFailures);
