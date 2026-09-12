using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Monitoring;

public sealed record FleetServerSummaryDto(
    Guid ServerId,
    string Name,
    GameServerKind Game,
    string Host,
    int QueryPort,
    bool IsEnabled,
    int PollIntervalSeconds,
    ServerStatus Status,
    DateTimeOffset? LastCheckedAtUtc,
    int? LatencyMs,
    string? CurrentMap,
    int? Players,
    int? MaxPlayers,
    int? Bots,
    int ConsecutiveFailures,
    int OpenIncidents,
    bool IsStale,
    bool RequiresAttention);
