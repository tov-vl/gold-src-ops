namespace GoldSrcOps.Application.Monitoring;

public enum PublicPlayerSearchState { Fresh, Busy, Unavailable, Invalid, Disabled }

public sealed record PublicPlayerSearchEntry(int Ordinal, string Name, int Rank, int Kills, int Deaths);

public sealed record PublicPlayerSearchResult(
    PublicPlayerSearchState State,
    DateTimeOffset? CapturedAtUtc,
    bool HasMore,
    IReadOnlyList<PublicPlayerSearchEntry> Entries,
    int? RetryAfterSeconds = null)
{
    public static PublicPlayerSearchResult Unavailable { get; } = new(PublicPlayerSearchState.Unavailable, null, false, []);
    public static PublicPlayerSearchResult Busy(int seconds) => new(PublicPlayerSearchState.Busy, null, false, [], Math.Clamp(seconds, 1, 10));
}

public sealed record PublicPlayerSearchSettings(bool Enabled, Guid ServerId);

public interface IPublicPlayerSearchSource
{
    Task<PublicPlayerSearchResult> LookupAsync(string name, PublicPlayerSearchMode mode, CancellationToken cancellationToken);
}
