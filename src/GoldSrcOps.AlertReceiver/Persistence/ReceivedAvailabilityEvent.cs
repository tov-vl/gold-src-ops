using GoldSrcOps.AlertReceiver.AvailabilityEvents;
using GoldSrcOps.AlertReceiver.Configuration;

namespace GoldSrcOps.AlertReceiver.Persistence;

internal sealed class ReceivedAvailabilityEvent
{
    private ReceivedAvailabilityEvent()
    {
    }

    private ReceivedAvailabilityEvent(
        AvailabilityEventRequest request,
        AvailabilityEventPayload payload,
        ReceiverMode receiverMode,
        DateTimeOffset receivedAtUtc)
    {
        Id = request.EventId;
        IncidentId = request.IncidentId;
        EventType = request.EventType;
        PayloadVersion = request.PayloadVersion;
        OccurredAtUtc = request.OccurredAtUtc;
        ReceivedAtUtc = receivedAtUtc;
        PayloadSha256 = payload.Sha256;
        Payload = payload.Json;
        ReceiverMode = receiverMode;
        ProviderActionSuppressed = receiverMode == ReceiverMode.CatchUp;
    }

    public Guid Id { get; private set; }

    public Guid IncidentId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public short PayloadVersion { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public string PayloadSha256 { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public ReceiverMode ReceiverMode { get; private set; }

    public bool ProviderActionSuppressed { get; private set; }

    public static ReceivedAvailabilityEvent Create(
        AvailabilityEventRequest request,
        AvailabilityEventPayload payload,
        ReceiverMode receiverMode,
        DateTimeOffset receivedAtUtc) =>
        new(request, payload, receiverMode, receivedAtUtc);
}
