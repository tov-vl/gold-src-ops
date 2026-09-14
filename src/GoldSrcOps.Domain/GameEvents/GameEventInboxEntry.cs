using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Domain.GameEvents;

public sealed class GameEventInboxEntry
{
    public const short CurrentContractVersion = 1;
    public const int MaxIntentHashLength = 64;
    public const int MaxMapLength = 128;
    public const int MaxPlayerCount = 255;

    private GameEventInboxEntry()
    {
        IntentHash = string.Empty;
    }

    public GameEventInboxEntry(
        Guid id,
        Guid serverId,
        Guid sourceInstanceId,
        long sequenceNumber,
        short contractVersion,
        GameEventType type,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset receivedAtUtc,
        string? map,
        int? players,
        int? bots,
        string intentHash)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Event id must not be empty.", nameof(id));
        }

        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }

        if (sourceInstanceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Source instance id must not be empty.",
                nameof(sourceInstanceId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequenceNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contractVersion);

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Game event type is not supported.");
        }

        var normalizedMap = NormalizeMap(map);
        if (RequiresMap(type) && normalizedMap is null)
        {
            throw new ArgumentException(
                "Map is required for map and round events.",
                nameof(map));
        }

        ValidatePopulation(players, bots);

        ArgumentException.ThrowIfNullOrWhiteSpace(intentHash);
        var normalizedHash = intentHash.Trim().ToUpperInvariant();
        if (normalizedHash.Length != MaxIntentHashLength ||
            normalizedHash.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Intent hash must be a SHA-256 hexadecimal value.",
                nameof(intentHash));
        }

        Id = id;
        ServerId = serverId;
        SourceInstanceId = sourceInstanceId;
        SequenceNumber = sequenceNumber;
        ContractVersion = contractVersion;
        Type = type;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        ReceivedAtUtc = receivedAtUtc.ToUniversalTime();
        Map = normalizedMap;
        Players = players;
        Bots = bots;
        IntentHash = normalizedHash;
    }

    public Guid Id { get; private set; }

    public Guid ServerId { get; private set; }

    public Guid SourceInstanceId { get; private set; }

    public long SequenceNumber { get; private set; }

    public short ContractVersion { get; private set; }

    public GameEventType Type { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public string? Map { get; private set; }

    public int? Players { get; private set; }

    public int? Bots { get; private set; }

    public string IntentHash { get; private set; }

    public Server Server { get; private set; } = null!;

    public static bool IsValidMapName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length > MaxMapLength ||
            !IsAsciiLetterOrDigit(normalized[0]) ||
            !IsAsciiLetterOrDigit(normalized[^1]))
        {
            return false;
        }

        return normalized.All(static character =>
            IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    public static bool RequiresMap(GameEventType type) =>
        type is GameEventType.MapStarted or GameEventType.RoundStarted or GameEventType.RoundEnded;

    private static string? NormalizeMap(string? map)
    {
        if (string.IsNullOrWhiteSpace(map))
        {
            return null;
        }

        var normalized = map.Trim();
        if (!IsValidMapName(normalized))
        {
            throw new ArgumentException(
                $"Map must be at most {MaxMapLength} characters and use ASCII letters, digits, underscores, or hyphens.",
                nameof(map));
        }

        return normalized;
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

        if (playerCount is < 0 or > MaxPlayerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(players),
                $"Players must be between 0 and {MaxPlayerCount}.");
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
