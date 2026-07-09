namespace GoldSrcOps.Application.Servers;

public sealed record UpdateServerCommand(
    string Name,
    string Host,
    int QueryPort,
    int? RconPort,
    int PollIntervalSeconds,
    string? Notes);
