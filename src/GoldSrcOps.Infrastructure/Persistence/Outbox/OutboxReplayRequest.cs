namespace GoldSrcOps.Infrastructure.Persistence.Outbox;

internal sealed class OutboxReplayRequest
{
    public const int MaxRequestedByLength = 200;
    public const int MaxReasonLength = 500;

    private OutboxReplayRequest()
    {
        EventType = string.Empty;
        AggregateType = string.Empty;
        RequestedBy = string.Empty;
        Reason = string.Empty;
    }

    public OutboxReplayRequest(
        Guid id,
        OutboxMessage message,
        string requestedBy,
        DateTimeOffset requestedAtUtc,
        string reason,
        DateTimeOffset nextAttemptAtUtc)
    {
        ValidateIdentifier(id, nameof(id));
        ArgumentNullException.ThrowIfNull(message);

        Id = id;
        OutboxMessageId = message.Id;
        EventType = message.EventType;
        PayloadVersion = message.PayloadVersion;
        AggregateType = message.AggregateType;
        AggregateId = message.AggregateId;
        OccurredAtUtc = message.OccurredAtUtc;
        RequestedBy = NormalizeRequiredText(
            requestedBy,
            MaxRequestedByLength,
            nameof(requestedBy));
        RequestedAtUtc = requestedAtUtc;
        Reason = NormalizeRequiredText(reason, MaxReasonLength, nameof(reason));
        ReplayNumber = checked(message.ReplayCount + 1);
        PreviousAttemptCount = message.AttemptCount;
        PreviousDeadLetteredAtUtc = message.DeadLetteredAtUtc;
        PreviousLastError = message.LastError;
        NextAttemptAtUtc = nextAttemptAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OutboxMessageId { get; private set; }

    public string EventType { get; private set; }

    public short PayloadVersion { get; private set; }

    public string AggregateType { get; private set; }

    public Guid AggregateId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string RequestedBy { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public string Reason { get; private set; }

    public int ReplayNumber { get; private set; }

    public int PreviousAttemptCount { get; private set; }

    public DateTimeOffset? PreviousDeadLetteredAtUtc { get; private set; }

    public string? PreviousLastError { get; private set; }

    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    private static void ValidateIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }
    }

    private static string NormalizeRequiredText(
        string? value,
        int maxLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"Value must not exceed {maxLength} characters.",
                parameterName);
        }

        return normalized;
    }
}
