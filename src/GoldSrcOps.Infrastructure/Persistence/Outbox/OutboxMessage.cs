namespace GoldSrcOps.Infrastructure.Persistence.Outbox;

internal sealed class OutboxMessage
{
    public const int MaxEventTypeLength = 128;
    public const int MaxAggregateTypeLength = 64;
    public const int MaxStatusLength = 32;
    public const int MaxErrorLength = 2000;

    private OutboxMessage()
    {
        EventType = string.Empty;
        AggregateType = string.Empty;
        Payload = string.Empty;
    }

    public OutboxMessage(
        Guid id,
        string eventType,
        short payloadVersion,
        string aggregateType,
        Guid aggregateId,
        DateTimeOffset occurredAtUtc,
        string payload)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Message id must not be empty.", nameof(id));
        }

        if (payloadVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadVersion),
                "Payload version must be positive.");
        }

        if (aggregateId == Guid.Empty)
        {
            throw new ArgumentException("Aggregate id must not be empty.", nameof(aggregateId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        Id = id;
        EventType = NormalizeRequiredText(eventType, MaxEventTypeLength, nameof(eventType));
        PayloadVersion = payloadVersion;
        AggregateType = NormalizeRequiredText(
            aggregateType,
            MaxAggregateTypeLength,
            nameof(aggregateType));
        AggregateId = aggregateId;
        OccurredAtUtc = occurredAtUtc;
        Payload = payload;
        Status = OutboxMessageStatus.Pending;
        NextAttemptAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    public string EventType { get; private set; }

    public short PayloadVersion { get; private set; }

    public string AggregateType { get; private set; }

    public Guid AggregateId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string Payload { get; private set; }

    public OutboxMessageStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public Guid? ClaimId { get; private set; }

    public DateTimeOffset? ClaimedAtUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }

    public int ReplayCount { get; private set; }

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
