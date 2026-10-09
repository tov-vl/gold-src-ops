namespace GoldSrcOps.Application.Monitoring;

public sealed record PublicLeaderboardEntry(int Position, string Name, int Rank, int Kills, int Deaths);

public sealed record PublicLeaderboardSnapshot(DateTimeOffset CapturedAtUtc, IReadOnlyList<PublicLeaderboardEntry> Entries);

public sealed record PublicLeaderboardProjection(string State, DateTimeOffset? CapturedAtUtc, IReadOnlyList<PublicLeaderboardEntry> Entries);

public sealed record PublicLeaderboardSettings(bool Enabled, Guid ServerId);
