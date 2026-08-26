namespace GoldSrcOps.Application.Alerts;

public interface IOutboxStore
{
    Task<ClaimedOutboxMessage?> ClaimNextPendingAsync(
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> MarkProcessedAsync(
        Guid messageId,
        Guid claimId,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> ScheduleRetryAsync(
        Guid messageId,
        Guid claimId,
        DateTimeOffset nextAttemptAtUtc,
        string lastError,
        CancellationToken cancellationToken);

    Task<bool> MarkDeadLetterAsync(
        Guid messageId,
        Guid claimId,
        string lastError,
        CancellationToken cancellationToken);

    Task<OutboxClaimRecoveryResult> RecoverExpiredClaimsAsync(
        DateTimeOffset expiredBeforeUtc,
        DateTimeOffset nextAttemptAtUtc,
        int maxAttempts,
        string retryError,
        string exhaustedError,
        CancellationToken cancellationToken);

    Task<int> DeleteProcessedBatchOlderThanAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken);

    Task<OutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
}
