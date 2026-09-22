namespace GoldSrcOps.Contracts.Monitoring;

public sealed record PublicServerJoinResponse(
    string Name,
    string Host,
    int Port,
    string State,
    string? Map,
    int? Players,
    int? MaxPlayers,
    DateTimeOffset? LastObservedAtUtc);
