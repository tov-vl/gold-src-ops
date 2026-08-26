namespace GoldSrcOps.Application.Alerts;

public enum AlertDispatchAttemptResultKind
{
    NoMessage,
    Delivered,
    RetryScheduled,
    DeadLettered,
    ClaimLost
}

public sealed record AlertDispatchAttemptResult(
    AlertDispatchAttemptResultKind Kind,
    Guid? MessageId,
    DateTimeOffset? NextAttemptAtUtc)
{
    public static AlertDispatchAttemptResult NoMessage() =>
        new(AlertDispatchAttemptResultKind.NoMessage, MessageId: null, NextAttemptAtUtc: null);

    public static AlertDispatchAttemptResult Delivered(Guid messageId) =>
        new(AlertDispatchAttemptResultKind.Delivered, messageId, NextAttemptAtUtc: null);

    public static AlertDispatchAttemptResult RetryScheduled(
        Guid messageId,
        DateTimeOffset nextAttemptAtUtc) =>
        new(AlertDispatchAttemptResultKind.RetryScheduled, messageId, nextAttemptAtUtc);

    public static AlertDispatchAttemptResult DeadLettered(Guid messageId) =>
        new(AlertDispatchAttemptResultKind.DeadLettered, messageId, NextAttemptAtUtc: null);

    public static AlertDispatchAttemptResult ClaimLost(Guid messageId) =>
        new(AlertDispatchAttemptResultKind.ClaimLost, messageId, NextAttemptAtUtc: null);
}
