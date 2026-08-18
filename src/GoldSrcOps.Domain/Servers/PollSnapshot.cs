namespace GoldSrcOps.Domain.Servers;

public sealed class PollSnapshot
{
    public const int MaxMapLength = 128;
    public const int MaxRawVersionLength = 128;
    public const int MaxFailureReasonLength = 2000;

    private PollSnapshot()
    {
    }

    public Guid Id { get; private set; }

    public Guid ServerId { get; private set; }

    public DateTimeOffset CheckedAtUtc { get; private set; }

    public bool IsReachable { get; private set; }

    public int? LatencyMs { get; private set; }

    public string? Map { get; private set; }

    public int? Players { get; private set; }

    public int? MaxPlayers { get; private set; }

    public int? Bots { get; private set; }

    public string? RawVersion { get; private set; }

    public string? FailureReason { get; private set; }

    public Server Server { get; private set; } = null!;

    public static PollSnapshot Reachable(
        Guid serverId,
        DateTimeOffset checkedAtUtc,
        int latencyMs,
        string map,
        int players,
        int maxPlayers,
        int bots,
        string? rawVersion)
    {
        return new PollSnapshot
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            CheckedAtUtc = checkedAtUtc,
            IsReachable = true,
            LatencyMs = latencyMs,
            Map = MonitoringText.NormalizeOptional(map, MaxMapLength),
            Players = players,
            MaxPlayers = maxPlayers,
            Bots = bots,
            RawVersion = MonitoringText.NormalizeOptional(rawVersion, MaxRawVersionLength),
            FailureReason = null
        };
    }

    public static PollSnapshot Unreachable(
        Guid serverId,
        DateTimeOffset checkedAtUtc,
        string failureReason)
    {
        return new PollSnapshot
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            CheckedAtUtc = checkedAtUtc,
            IsReachable = false,
            FailureReason = MonitoringText.NormalizeRequired(
                failureReason,
                "Server query failed.",
                MaxFailureReasonLength)
        };
    }
}
