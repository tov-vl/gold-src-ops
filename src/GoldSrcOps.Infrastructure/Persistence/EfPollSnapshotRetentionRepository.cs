using GoldSrcOps.Application.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfPollSnapshotRetentionRepository : IPollSnapshotRetentionRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfPollSnapshotRetentionRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> DeleteBatchOlderThanAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var snapshotIds = _dbContext.PollSnapshots
            .Where(x => x.CheckedAtUtc < cutoffUtc)
            .OrderBy(x => x.CheckedAtUtc)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .Select(x => x.Id);

        return await _dbContext.PollSnapshots
            .Where(x => snapshotIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
