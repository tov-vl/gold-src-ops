namespace GoldSrcOps.Application.Monitoring;

public interface IPollSnapshotRetentionRepository
{
    Task<int> DeleteBatchOlderThanAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken);
}
