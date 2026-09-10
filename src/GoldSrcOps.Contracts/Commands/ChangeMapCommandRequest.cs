namespace GoldSrcOps.Contracts.Commands;

public sealed record ChangeMapCommandRequest(string Map)
{
    public const int MaxMapNameLength = 128;

    public static bool IsValidMapName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Length > MaxMapNameLength ||
            !IsAsciiLetterOrDigit(value[0]) ||
            !IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }

        return value.All(static character =>
            IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';
}
