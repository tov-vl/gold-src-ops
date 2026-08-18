namespace GoldSrcOps.Domain.Servers;

public sealed class ServerCurrentState
{
    public const int MaxMapLength = 128;
    public const int MaxFailureReasonLength = 2000;

    private ServerCurrentState()
    {
    }

    private ServerCurrentState(Guid serverId, DateTimeOffset checkedAtUtc)
    {
        ServerId = serverId;
        Status = ServerStatus.Unknown;
        IsReachable = false;
        LastCheckedAtUtc = checkedAtUtc;
    }

    public Guid ServerId { get; private set; }

    public ServerStatus Status { get; private set; }

    public bool IsReachable { get; private set; }

    public DateTimeOffset LastCheckedAtUtc { get; private set; }

    public DateTimeOffset? LastSuccessAtUtc { get; private set; }

    public int? LatencyMs { get; private set; }

    public string? CurrentMap { get; private set; }

    public int? Players { get; private set; }

    public int? MaxPlayers { get; private set; }

    public string? FailureReason { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public Server Server { get; private set; } = null!;

    public static ServerCurrentState CreateUnknown(Guid serverId, DateTimeOffset checkedAtUtc) =>
        new(serverId, checkedAtUtc);

    public void MarkOnline(
        DateTimeOffset checkedAtUtc,
        int latencyMs,
        string map,
        int players,
        int maxPlayers)
    {
        Status = ServerStatus.Online;
        IsReachable = true;
        LastCheckedAtUtc = checkedAtUtc;
        LastSuccessAtUtc = checkedAtUtc;
        LatencyMs = latencyMs;
        CurrentMap = MonitoringText.NormalizeOptional(map, MaxMapLength);
        Players = players;
        MaxPlayers = maxPlayers;
        FailureReason = null;
        ConsecutiveFailures = 0;
    }

    public void MarkOffline(DateTimeOffset checkedAtUtc, string failureReason)
    {
        Status = ServerStatus.Offline;
        IsReachable = false;
        LastCheckedAtUtc = checkedAtUtc;
        LatencyMs = null;
        CurrentMap = null;
        Players = null;
        MaxPlayers = null;
        FailureReason = MonitoringText.NormalizeRequired(
            failureReason,
            "Server query failed.",
            MaxFailureReasonLength);
        ConsecutiveFailures = ConsecutiveFailures == int.MaxValue
            ? int.MaxValue
            : ConsecutiveFailures + 1;
    }
}
