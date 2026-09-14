namespace GoldSrcOps.GameEventAgent;

internal static class GameEventContractRules
{
    public const short ContractVersion = 1;
    public const int MaximumMapLength = 128;
    public const int MaximumPlayerCount = 255;

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "server.started",
        "server.stopped",
        "map.started",
        "round.started",
        "round.ended"
    };

    public static GameEventSourceInput ValidateAndNormalize(
        GameEventSourceInput input,
        DateTimeOffset enqueuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(input);

        var type = input.Type?.Trim();
        if (type is null || !AllowedTypes.Contains(type))
        {
            throw new ArgumentException("Game event type is not supported.", nameof(input));
        }

        if (input.OccurredAtUtc == default)
        {
            throw new ArgumentException("OccurredAtUtc is required.", nameof(input));
        }

        var map = string.IsNullOrWhiteSpace(input.Map) ? null : input.Map.Trim();
        if (RequiresMap(type) && map is null)
        {
            throw new ArgumentException("Map is required for map and round events.", nameof(input));
        }

        if (map is not null && !IsValidMapName(map))
        {
            throw new ArgumentException(
                $"Map must be at most {MaximumMapLength} characters and use ASCII letters, digits, underscores, or hyphens.",
                nameof(input));
        }

        ValidatePopulation(input.Players, input.Bots);

        var occurredAtUtc = input.OccurredAtUtc.ToUniversalTime();
        if (occurredAtUtc > enqueuedAtUtc.ToUniversalTime() + TimeSpan.FromMinutes(5))
        {
            throw new ArgumentException(
                "OccurredAtUtc must not be more than five minutes in the future.",
                nameof(input));
        }

        return input with
        {
            Type = type,
            OccurredAtUtc = occurredAtUtc,
            Map = map
        };
    }

    private static bool RequiresMap(string type) =>
        type is "map.started" or "round.started" or "round.ended";

    private static bool IsValidMapName(string value)
    {
        if (value.Length > MaximumMapLength ||
            !IsAsciiLetterOrDigit(value[0]) ||
            !IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }

        return value.All(static character =>
            IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static void ValidatePopulation(int? players, int? bots)
    {
        if ((players is null) != (bots is null))
        {
            throw new ArgumentException(
                "Players and bots must either both be supplied or both be omitted.",
                nameof(players));
        }

        if (players is not int playerCount || bots is not int botCount)
        {
            return;
        }

        if (playerCount is < 0 or > MaximumPlayerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(players),
                $"Players must be between 0 and {MaximumPlayerCount}.");
        }

        if (botCount < 0 || botCount > playerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bots),
                "Bots must be non-negative and no greater than players.");
        }
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';
}
