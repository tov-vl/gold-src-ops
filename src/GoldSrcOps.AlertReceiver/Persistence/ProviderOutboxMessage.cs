namespace GoldSrcOps.AlertReceiver.Persistence;

internal sealed class ProviderOutboxMessage
{
    public const int MaxErrorLength = 2000;

    private ProviderOutboxMessage()
    {
    }

    private ProviderOutboxMessage(
        Guid sourceEventId,
        Guid incidentId,
        ProviderOutboxAction action,
        string payload,
        DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        SourceEventId = sourceEventId;
        IncidentId = incidentId;
        Action = action;
        CreatedAtUtc = createdAtUtc;
        Payload = payload;
        Status = ProviderOutboxStatus.Pending;
        NextAttemptAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid SourceEventId { get; private set; }

    public Guid IncidentId { get; private set; }

    public ProviderOutboxAction Action { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string Payload { get; private set; } = string.Empty;

    public ProviderOutboxStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public Guid? ClaimId { get; private set; }

    public DateTimeOffset? ClaimedAtUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public static ProviderOutboxMessage Create(
        Guid sourceEventId,
        Guid incidentId,
        ProviderOutboxAction action,
        string payload,
        DateTimeOffset createdAtUtc) =>
        new(sourceEventId, incidentId, action, payload, createdAtUtc);
}

internal enum ProviderOutboxAction
{
    Trigger,
    Resolve,
}

internal enum ProviderOutboxStatus
{
    Pending,
    Processing,
    Processed,
    DeadLetter,
}
