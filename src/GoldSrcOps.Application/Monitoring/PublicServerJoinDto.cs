namespace GoldSrcOps.Application.Monitoring;

public enum PublicServerJoinState
{
    Unknown = 0,
    Online = 1,
    Offline = 2
}

public sealed record PublicServerJoinDto(
    PublicServerJoinState State,
    string? Map,
    int? Players,
    int? MaxPlayers,
    DateTimeOffset? LastObservedAtUtc);
