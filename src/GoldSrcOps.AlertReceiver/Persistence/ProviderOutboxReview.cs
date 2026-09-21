namespace GoldSrcOps.AlertReceiver.Persistence;

internal sealed class ProviderOutboxReview
{
    public const int MaxRequestedByLength = 200;
    public const int MaxReasonLength = 500;

    private ProviderOutboxReview()
    {
    }

    private ProviderOutboxReview(
        Guid requestId,
        Guid messageId,
        string requestedBy,
        string reason,
        DateTimeOffset reviewedAtUtc)
    {
        RequestId = requestId;
        MessageId = messageId;
        RequestedBy = requestedBy;
        Reason = reason;
        ReviewedAtUtc = reviewedAtUtc;
    }

    public Guid RequestId { get; private set; }

    public Guid MessageId { get; private set; }

    public string RequestedBy { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset ReviewedAtUtc { get; private set; }

    public static ProviderOutboxReview Create(
        Guid requestId,
        Guid messageId,
        string requestedBy,
        string reason,
        DateTimeOffset reviewedAtUtc) =>
        new(requestId, messageId, requestedBy, reason, reviewedAtUtc);
}
