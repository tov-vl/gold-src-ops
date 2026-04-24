namespace GoldSrcOps.Domain.Servers;

public sealed class AvailabilityIncident
{
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
}
