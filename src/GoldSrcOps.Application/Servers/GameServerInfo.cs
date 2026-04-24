namespace GoldSrcOps.Application.Servers;

public sealed record GameServerInfo(
    string ResponseFormat,
    string Name,
    string Map,
    string Folder,
    string Game,
    int Protocol,
    int Players,
    int MaxPlayers,
    int Bots,
    char ServerType,
    char Environment,
    bool IsPrivate,
    bool HasVac,
    string? Version,
    TimeSpan Latency);
