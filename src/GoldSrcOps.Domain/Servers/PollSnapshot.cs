namespace GoldSrcOps.Domain.Servers;

public sealed class PollSnapshot
{
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
            Map = string.IsNullOrWhiteSpace(map) ? null : map.Trim(),
            Players = players,
            MaxPlayers = maxPlayers,
            Bots = bots,
            RawVersion = string.IsNullOrWhiteSpace(rawVersion) ? null : rawVersion.Trim(),
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
            FailureReason = string.IsNullOrWhiteSpace(failureReason)
                ? "Server query failed."
                : failureReason.Trim()
        };
    }
}
