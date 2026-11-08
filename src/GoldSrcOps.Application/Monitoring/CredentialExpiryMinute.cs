using System.Globalization;

namespace GoldSrcOps.Application.Monitoring;

public static class CredentialExpiryMinute
{
    public static bool TryParse(string? value, out DateTimeOffset start)
    {
        start = default;
        return value is { Length: 17 } && DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out start);
    }
}
