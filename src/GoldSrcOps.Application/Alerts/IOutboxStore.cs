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

    Task<int> RecoverExpiredClaimsAsync(
        DateTimeOffset expiredBeforeUtc,
        DateTimeOffset nextAttemptAtUtc,
        string lastError,
        CancellationToken cancellationToken);
}
