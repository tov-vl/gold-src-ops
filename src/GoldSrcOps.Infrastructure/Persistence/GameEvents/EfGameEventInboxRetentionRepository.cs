using GoldSrcOps.Application.GameEvents;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence.GameEvents;

internal sealed class EfGameEventInboxRetentionRepository : IGameEventInboxRetentionRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfGameEventInboxRetentionRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> DeleteBatchReceivedBeforeAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var eventIds = _dbContext.GameEventInbox
            .Where(entry => entry.ReceivedAtUtc < cutoffUtc)
            .OrderBy(entry => entry.ReceivedAtUtc)
            .ThenBy(entry => entry.Id)
            .Take(batchSize)
            .Select(entry => entry.Id);

        return await _dbContext.GameEventInbox
            .Where(entry => eventIds.Contains(entry.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
