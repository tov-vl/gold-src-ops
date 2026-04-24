namespace GoldSrcOps.Contracts.Servers;

public sealed record RegisterServerRequest(
    string Name,
    string Host,
    int QueryPort,
    int? RconPort,
    int? PollIntervalSeconds,
    string? Notes);
