using System.Text;

namespace GoldSrcOps.Application.Monitoring;

public static class PublicPlayerName
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryNormalize(string? value, out string name)
    {
        name = string.Empty;
        if (value is null || value.Length > 256 || value.Any(char.IsControl) ||
            value.Contains("STEAM_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trimmed = value.Trim(' ');
        try
        {
            if (StrictUtf8.GetByteCount(trimmed) is < 1 or > 31)
            {
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        name = trimmed;
        return true;
    }

    public static string? DecodeHex(string hex)
    {
        if (hex.Length is < 2 or > 62 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
        {
            return null;
        }

        try
        {
            var decoded = StrictUtf8.GetString(Convert.FromHexString(hex));
            return TryNormalize(decoded, out var name) && string.Equals(name, decoded, StringComparison.Ordinal)
                ? name : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
