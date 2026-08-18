namespace GoldSrcOps.Domain.Servers;

public sealed class AvailabilityIncident
{
    public const int MaxReasonLength = 2000;

    private AvailabilityIncident()
    {
        StartReason = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ServerId { get; private set; }

    public IncidentType Type { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public string StartReason { get; private set; }

    public string? EndReason { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public Server Server { get; private set; } = null!;

    public bool IsOpen => ClosedAtUtc is null;

    public static AvailabilityIncident Open(
        Guid serverId,
        DateTimeOffset openedAtUtc,
        string startReason,
        int consecutiveFailures)
    {
        return new AvailabilityIncident
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            Type = IncidentType.Unreachable,
            OpenedAtUtc = openedAtUtc,
            StartReason = MonitoringText.NormalizeRequired(
                startReason,
                "Server became unreachable.",
                MaxReasonLength),
            ConsecutiveFailures = Math.Max(1, consecutiveFailures)
        };
    }

    public void RecordFailure(int consecutiveFailures)
    {
        if (!IsOpen)
        {
            return;
        }

        ConsecutiveFailures = Math.Max(ConsecutiveFailures, consecutiveFailures);
    }

    public void Close(DateTimeOffset closedAtUtc, string endReason)
    {
        if (!IsOpen)
        {
            return;
        }

        ClosedAtUtc = closedAtUtc;
        EndReason = MonitoringText.NormalizeRequired(
            endReason,
            "Server query recovered.",
            MaxReasonLength);
    }
}
