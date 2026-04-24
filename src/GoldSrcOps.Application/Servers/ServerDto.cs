using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Servers;

public sealed record ServerDto(
    Guid Id,
    string Name,
    GameServerKind Game,
    string Host,
    int QueryPort,
    int? RconPort,
    bool IsEnabled,
    int PollIntervalSeconds,
    string? Notes,
    DateTimeOffset CreatedAtUtc);
