namespace GoldSrcOps.Domain.Servers;

public static class RconSecretReference
{
    public const string Scheme = "rcon-secret://";
    public const int MaxAliasLength = 128;

    public static string Create(string secretAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAlias);

        var normalizedAlias = secretAlias.Trim().ToLowerInvariant();
        if (!IsValidNormalizedAlias(normalizedAlias))
        {
            throw new ArgumentException(
                $"RCON secret alias must be at most {MaxAliasLength} characters, use ASCII letters, digits, '.', '_', or '-', and start and end with a letter or digit.",
                nameof(secretAlias));
        }

        return string.Concat(Scheme, normalizedAlias);
    }

    public static bool IsValidAlias(string? secretAlias)
    {
        return !string.IsNullOrWhiteSpace(secretAlias) &&
            IsValidNormalizedAlias(secretAlias.Trim());
    }

    public static bool TryGetAlias(string? secretReference, out string secretAlias)
    {
        secretAlias = string.Empty;
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return false;
        }

        var normalizedReference = secretReference.Trim();
        if (!normalizedReference.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = normalizedReference[Scheme.Length..];
        if (!IsValidAlias(candidate))
        {
            return false;
        }

        secretAlias = candidate.Trim().ToLowerInvariant();
        return true;
    }

    public static string Normalize(string secretReference)
    {
        if (!TryGetAlias(secretReference, out var secretAlias))
        {
            throw new ArgumentException(
                $"RCON secret reference must use the '{Scheme}<alias>' format.",
                nameof(secretReference));
        }

        return Create(secretAlias);
    }

    private static bool IsValidNormalizedAlias(string secretAlias)
    {
        if (secretAlias.Length is 0 or > MaxAliasLength ||
            !IsAsciiLetterOrDigit(secretAlias[0]) ||
            !IsAsciiLetterOrDigit(secretAlias[^1]))
        {
            return false;
        }

        foreach (var character in secretAlias)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
