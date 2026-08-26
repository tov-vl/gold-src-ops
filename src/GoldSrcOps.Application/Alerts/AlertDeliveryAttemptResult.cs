namespace GoldSrcOps.Application.Alerts;

public enum AlertDeliveryAttemptResultKind
{
    Delivered,
    RetryableFailure,
    PermanentFailure
}

public enum AlertDeliveryFailureCategory
{
    Transport,
    Timeout,
    RemoteResponse,
    Unexpected
}

public sealed record AlertDeliveryAttemptResult(
    AlertDeliveryAttemptResultKind Kind,
    AlertDeliveryFailureCategory? FailureCategory,
    int? RemoteStatusCode,
    TimeSpan? RetryAfter)
{
    public static AlertDeliveryAttemptResult Delivered() =>
        new(
            AlertDeliveryAttemptResultKind.Delivered,
            FailureCategory: null,
            RemoteStatusCode: null,
            RetryAfter: null);

    public static AlertDeliveryAttemptResult RetryableFailure(
        AlertDeliveryFailureCategory failureCategory,
        int? remoteStatusCode = null,
        TimeSpan? retryAfter = null) =>
        new(
            AlertDeliveryAttemptResultKind.RetryableFailure,
            failureCategory,
            remoteStatusCode,
            retryAfter);

    public static AlertDeliveryAttemptResult PermanentFailure(
        AlertDeliveryFailureCategory failureCategory,
        int? remoteStatusCode = null) =>
        new(
            AlertDeliveryAttemptResultKind.PermanentFailure,
            failureCategory,
            remoteStatusCode,
            RetryAfter: null);
}
