namespace GoldSrcOps.Contracts.Servers;

public sealed record ServerResponse(
    Guid Id,
    string Name,
    string Game,
    string Host,
    int QueryPort,
    int? RconPort,
    bool IsEnabled,
    int PollIntervalSeconds,
    string? Notes,
    DateTimeOffset CreatedAtUtc);
