using System.Globalization;
using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal static class PublicPlayerSearchProtocol
{
    public static PublicPlayerSearchResult Parse(string response, string nonce, string name, DateTimeOffset now, PublicPlayerSearchMode mode = PublicPlayerSearchMode.Exact)
    {
        var version = mode switch
        {
            PublicPlayerSearchMode.Exact => "1",
            PublicPlayerSearchMode.Contains => "2",
            _ => throw InvalidFrame()
        };
        if (!PublicPlayerSearchMatching.TryNormalize(name, mode, out var normalized) ||
            !string.Equals(name, normalized, StringComparison.Ordinal))
        {
            throw InvalidFrame();
        }

        if (response.Length is 0 or > 2048 || response.Any(static c => c > 127 || (char.IsControl(c) && c is not ('\n' or '\r'))))
        {
            throw InvalidFrame();
        }

        var lines = response.TrimEnd('\r', '\n').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 1)
        {
            if (string.Equals(lines[0], $"GSLOOKUP {version} {nonce} unavailable", StringComparison.Ordinal) ||
                string.Equals(lines[0], $"GSLOOKUP {version} invalid", StringComparison.Ordinal))
            {
                return PublicPlayerSearchResult.Unavailable;
            }

            var busy = lines[0].Split(' ');
            if (busy.Length == 5 && string.Equals(busy[0], "GSLOOKUP", StringComparison.Ordinal) &&
                string.Equals(busy[1], version, StringComparison.Ordinal) && string.Equals(busy[2], nonce, StringComparison.Ordinal) &&
                string.Equals(busy[3], "busy", StringComparison.Ordinal))
            {
                return PublicPlayerSearchResult.Busy(Number(busy[4], 1, 10));
            }
        }

        var header = lines[0].Split(' ');
        if (header.Length != 6 || !string.Equals(header[0], "GSLOOKUP", StringComparison.Ordinal) ||
            !string.Equals(header[1], version, StringComparison.Ordinal) || !string.Equals(header[2], nonce, StringComparison.Ordinal) ||
            !long.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            throw InvalidFrame();
        }

        var count = Number(header[4], 0, 10);
        var hasMore = Number(header[5], 0, 1) == 1;
        if ((hasMore && count != 10) || lines.Length != count + 2 ||
            !string.Equals(lines[^1], $"GSPEND {version} {nonce} {header[3]} {header[4]} {header[5]}", StringComparison.Ordinal))
        {
            throw InvalidFrame();
        }

        DateTimeOffset captured;
        try
        {
            captured = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidFrame();
        }

        if (captured > now.AddSeconds(30) || captured <= now.AddSeconds(-60))
        {
            throw InvalidFrame();
        }

        var entries = new List<PublicPlayerSearchEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var fields = lines[index + 1].Split(' ');
            if (fields.Length != 6 || !string.Equals(fields[0], "GSPROW", StringComparison.Ordinal) ||
                Number(fields[1], 1, 10) != index + 1)
            {
                throw InvalidFrame();
            }

            var rank = Number(fields[2], 0, 4);
            var kills = Number(fields[3], 0, 1_000_000_000);
            var deaths = Number(fields[4], 0, 1_000_000_000);
            var resultName = PublicPlayerName.DecodeHex(fields[5]);
            var expectedRank = kills >= 500 ? 4 : kills >= 250 ? 3 : kills >= 100 ? 2 : kills >= 25 ? 1 : 0;
            if (rank != expectedRank || (kills == 0 && deaths == 0) ||
                (entries.Count > 0 && (kills > entries[^1].Kills || (kills == entries[^1].Kills && deaths < entries[^1].Deaths))) ||
                (resultName is null || !PublicPlayerSearchMatching.Matches(resultName, name, mode)))
            {
                throw InvalidFrame();
            }

            entries.Add(new(index + 1, resultName, rank, kills, deaths));
        }

        return new(PublicPlayerSearchState.Fresh, captured, hasMore, entries.AsReadOnly());
    }

    private static int Number(string value, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= min && parsed <= max
            ? parsed : throw InvalidFrame();

    private static InvalidDataException InvalidFrame() => new("Public player lookup frame is invalid.");
}
