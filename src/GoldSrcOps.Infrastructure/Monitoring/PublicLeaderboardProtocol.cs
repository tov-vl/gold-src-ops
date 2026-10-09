using System.Globalization;
using System.Text;
using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal static class PublicLeaderboardProtocol
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PublicLeaderboardSnapshot? Parse(string response, DateTimeOffset now)
    {
        if (response.Length > 2048)
        {
            throw InvalidFrame();
        }

        var lines = response.Trim().Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length == 1 && string.Equals(lines[0], "GSLEADER 1 unavailable", StringComparison.Ordinal))
        {
            return null;
        }

        var header = lines[0].Split(' ');
        if (header.Length != 4 || !string.Equals(header[0], "GSLEADER", StringComparison.Ordinal) || !string.Equals(header[1], "1", StringComparison.Ordinal) ||
            !long.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 0 or > 10 ||
            lines.Length != count + 2 || !string.Equals(lines[^1], $"GSLEND 1 {header[2]} {header[3]}", StringComparison.Ordinal))
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

        if (captured > now.AddSeconds(30) || captured < now.AddMinutes(-2))
        {
            throw InvalidFrame();
        }

        var entries = new List<PublicLeaderboardEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var fields = lines[index + 1].Split(' ');
            if (fields.Length != 6 || !string.Equals(fields[0], "GSLROW", StringComparison.Ordinal) ||
                Number(fields[1], 1, 10) != index + 1)
            {
                throw InvalidFrame();
            }

            var rank = Number(fields[2], 0, 4);
            var kills = Number(fields[3], 0, 1_000_000_000);
            var deaths = Number(fields[4], 0, 1_000_000_000);
            var expectedRank = kills >= 500 ? 4 : kills >= 250 ? 3 : kills >= 100 ? 2 : kills >= 25 ? 1 : 0;
            if (rank != expectedRank || (kills == 0 && deaths == 0) ||
                (entries.Count > 0 && (kills > entries[^1].Kills ||
                 (kills == entries[^1].Kills && deaths < entries[^1].Deaths))))
            {
                throw InvalidFrame();
            }

            var name = DecodeName(fields[5]);
            entries.Add(new(index + 1, name, rank, kills, deaths));
        }

        return new(captured, entries.AsReadOnly());
    }

    private static string DecodeName(string hex)
    {
        if (string.Equals(hex, "-", StringComparison.Ordinal))
        {
            return "Игрок";
        }

        if (hex.Length is < 2 or > 62 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
        {
            throw InvalidFrame();
        }

        string name;
        try
        {
            name = StrictUtf8.GetString(Convert.FromHexString(hex));
        }
        catch (DecoderFallbackException)
        {
            throw InvalidFrame();
        }

        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
        {
            throw InvalidFrame();
        }

        return name.Contains("STEAM_", StringComparison.OrdinalIgnoreCase) ? "Игрок" : name;
    }

    private static int Number(string value, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= min && parsed <= max
            ? parsed : throw InvalidFrame();

    private static InvalidDataException InvalidFrame() => new("Public leaderboard frame is invalid.");
}
