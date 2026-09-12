namespace GoldSrcOps.Contracts.Monitoring;

public sealed record FleetServerSummaryResponse(
    Guid ServerId,
    string Name,
    string Game,
    string Host,
    int QueryPort,
    bool IsEnabled,
    int PollIntervalSeconds,
    string Status,
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
