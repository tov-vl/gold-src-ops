using System.Text;

namespace GoldSrcOps.Application.Monitoring;

public enum PublicPlayerSearchMode { Exact, Contains }

public static class PublicPlayerSearchMatching
{
    public static bool TryParseMode(string? value, out PublicPlayerSearchMode mode)
    {
        mode = value switch
        {
            "exact" => PublicPlayerSearchMode.Exact,
            "contains" => PublicPlayerSearchMode.Contains,
            _ => (PublicPlayerSearchMode)(-1)
        };
        return mode is PublicPlayerSearchMode.Exact or PublicPlayerSearchMode.Contains;
    }

    public static bool TryNormalize(string? value, PublicPlayerSearchMode mode, out string name)
    {
        name = string.Empty;
        if (mode is not (PublicPlayerSearchMode.Exact or PublicPlayerSearchMode.Contains) ||
            !PublicPlayerName.TryNormalize(value, out var normalized))
        {
            return false;
        }

        if (mode == PublicPlayerSearchMode.Contains)
        {
            var scalars = 0;
            foreach (var _ in normalized.EnumerateRunes()) { scalars++; }
            if (scalars < 3) { return false; }
        }

        name = normalized;
        return true;
    }

    public static bool Matches(string name, string query, PublicPlayerSearchMode mode) => mode switch
    {
        PublicPlayerSearchMode.Exact => string.Equals(name, query, StringComparison.Ordinal),
        PublicPlayerSearchMode.Contains => Fold(name).Contains(Fold(query), StringComparison.Ordinal),
        _ => false
    };

    // Keep the addon v2 map exact: other scripts, combining marks and surrogates stay literal.
    private static string Fold(string value) => string.Create(value.Length, value, static (output, input) =>
    {
        for (var index = 0; index < input.Length; index++)
        {
            output[index] = input[index] switch
            {
                >= 'A' and <= 'Z' or >= '\u0410' and <= '\u042F' => (char)(input[index] + 32),
                '\u0401' => '\u0451',
                _ => input[index]
            };
        }
    });
}
