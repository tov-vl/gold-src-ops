namespace GoldSrcOps.Domain.Servers;

internal static class MonitoringText
{
    public static string? NormalizeOptional(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Truncate(value.Trim(), maxLength);
    }

    public static string NormalizeRequired(string? value, string fallback, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        return NormalizeOptional(value, maxLength)
            ?? NormalizeOptional(fallback, maxLength)!;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}
