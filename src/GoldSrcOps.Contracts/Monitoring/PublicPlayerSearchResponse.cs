using System.Text.Json.Serialization;

namespace GoldSrcOps.Contracts.Monitoring;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublicPlayerSearchRequest(string Name, string? Match = "exact");

public sealed record PublicPlayerSearchResponse(
    string State,
    DateTimeOffset? CapturedAtUtc,
    bool HasMore,
    IReadOnlyList<PublicPlayerSearchEntryResponse> Entries,
    int? RetryAfterSeconds);

public sealed record PublicPlayerSearchEntryResponse(int Ordinal, string Name, int Rank, int Kills, int Deaths);
