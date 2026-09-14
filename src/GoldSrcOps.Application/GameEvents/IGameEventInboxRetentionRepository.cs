namespace GoldSrcOps.Application.GameEvents;

public interface IGameEventInboxRetentionRepository
{
    Task<int> DeleteBatchReceivedBeforeAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken);
}
