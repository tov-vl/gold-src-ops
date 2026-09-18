using System.Text.Json;

namespace GoldSrcOps.AlertReceiver.ProviderDelivery;

internal sealed record ClaimedProviderOutboxMessage(
    Guid Id,
    Guid SourceEventId,
    Guid IncidentId,
    string Action,
    DateTimeOffset CreatedAtUtc,
    JsonElement Payload,
    int AttemptCount,
    Guid ClaimId,
    DateTimeOffset ClaimedAtUtc);

internal enum ProviderDeliveryResultKind
{
    Delivered,
    RetryableFailure,
    PermanentFailure,
}

internal sealed record ProviderDeliveryResult(
    ProviderDeliveryResultKind Kind,
    string FailureSummary,
    TimeSpan? RetryAfter = null)
{
    public static ProviderDeliveryResult Delivered() =>
        new(ProviderDeliveryResultKind.Delivered, string.Empty);

    public static ProviderDeliveryResult Retryable(string summary, TimeSpan? retryAfter = null) =>
        new(ProviderDeliveryResultKind.RetryableFailure, summary, retryAfter);

    public static ProviderDeliveryResult Permanent(string summary) =>
        new(ProviderDeliveryResultKind.PermanentFailure, summary);
}

internal sealed record ProviderOutboxStatistics(
    long PendingCount,
    long ProcessingCount,
    long DeadLetterCount,
    DateTimeOffset? OldestPendingAtUtc);

internal sealed record ProviderClaimRecoveryResult(int RetryScheduled, int DeadLettered)
{
    public int Total => RetryScheduled + DeadLettered;
}

internal interface IProviderDeliveryChannel
{
    Task<ProviderDeliveryResult> DeliverAsync(
        ClaimedProviderOutboxMessage message,
        CancellationToken cancellationToken);
}

internal interface IProviderRetryDelayProvider
{
    TimeSpan GetDelay(int attemptCount);
}

internal interface IProviderOutboxStore
{
    Task<ClaimedProviderOutboxMessage?> ClaimNextAsync(
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> MarkProcessedAsync(Guid messageId, Guid claimId, DateTimeOffset processedAtUtc, CancellationToken cancellationToken);

    Task<bool> ScheduleRetryAsync(Guid messageId, Guid claimId, DateTimeOffset nextAttemptAtUtc, string error, CancellationToken cancellationToken);

    Task<bool> MarkDeadLetterAsync(Guid messageId, Guid claimId, DateTimeOffset deadLetteredAtUtc, string error, CancellationToken cancellationToken);

    Task<ProviderClaimRecoveryResult> RecoverExpiredClaimsAsync(DateTimeOffset expiredBeforeUtc, DateTimeOffset recoveredAtUtc, int maxAttempts, CancellationToken cancellationToken);

    Task<int> DeleteProcessedBatchAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken);

    Task<ProviderOutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
}
