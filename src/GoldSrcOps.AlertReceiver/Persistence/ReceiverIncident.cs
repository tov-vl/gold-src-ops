using GoldSrcOps.AlertReceiver.AvailabilityEvents;

namespace GoldSrcOps.AlertReceiver.Persistence;

internal sealed class ReceiverIncident
{
    private ReceiverIncident()
    {
    }

    private ReceiverIncident(AvailabilityEventRequest request)
    {
        Id = request.IncidentId;
        ServerId = request.ServerId;
        ServerName = request.ServerName;
        State = ReceiverIncidentState.Open;
        OpenedAtUtc = request.OpenedAtUtc;
        ConsecutiveFailures = request.ConsecutiveFailures;
        OpenReason = request.Reason;
        LastEventId = request.EventId;
        LastEventAtUtc = request.OccurredAtUtc;
        Revision = 1;
    }

    public Guid Id { get; private set; }

    public Guid ServerId { get; private set; }

    public string ServerName { get; private set; } = string.Empty;

    public ReceiverIncidentState State { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public string OpenReason { get; private set; } = string.Empty;

    public string? ResolutionReason { get; private set; }

    public Guid LastEventId { get; private set; }

    public DateTimeOffset LastEventAtUtc { get; private set; }

    public long Revision { get; private set; }

    public static ReceiverIncident Open(AvailabilityEventRequest request) => new(request);

    public string? TryResolve(AvailabilityEventRequest request)
    {
        if (State != ReceiverIncidentState.Open)
        {
            return "Incident has already been resolved.";
        }

        if (request.ServerId != ServerId ||
            !string.Equals(request.ServerName, ServerName, StringComparison.Ordinal) ||
            request.OpenedAtUtc != OpenedAtUtc ||
            request.ConsecutiveFailures != ConsecutiveFailures)
        {
            return "Recovery event does not match the recorded incident identity.";
        }

        State = ReceiverIncidentState.Resolved;
        ClosedAtUtc = request.ClosedAtUtc;
        ResolutionReason = request.Reason;
        LastEventId = request.EventId;
        LastEventAtUtc = request.OccurredAtUtc;
        Revision++;

        return null;
    }
}

internal enum ReceiverIncidentState
{
    Open,
    Resolved,
}
