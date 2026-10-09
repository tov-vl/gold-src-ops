namespace GoldSrcOps.Contracts.Monitoring;

public sealed record PublicLeaderboardResponse(
    string State,
    DateTimeOffset? CapturedAtUtc,
    IReadOnlyList<PublicLeaderboardEntryResponse> Entries);

public sealed record PublicLeaderboardEntryResponse(int Position, string Name, int Rank, int Kills, int Deaths);
