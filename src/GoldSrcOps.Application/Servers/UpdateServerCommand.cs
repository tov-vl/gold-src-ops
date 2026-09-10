namespace GoldSrcOps.Application.Servers;

public sealed record UpdateServerCommand(
    long ExpectedRevision,
    string Name,
    string Host,
    int QueryPort,
    int? RconPort,
    int PollIntervalSeconds,
    string? Notes);
