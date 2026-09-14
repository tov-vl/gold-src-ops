using GoldSrcOps.Application.GameEvents;
using GoldSrcOps.Domain.GameEvents;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence.GameEvents;

internal sealed class EfGameEventReadRepository : IGameEventReadRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfGameEventReadRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .AnyAsync(server => server.Id == serverId, cancellationToken);
    }

    public async Task<IReadOnlyList<GameEventHistoryItemDto>> ListRecentRoundEndedAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken)
    {
        return await _dbContext.GameEventInbox
            .AsNoTracking()
            .Where(entry =>
                entry.ServerId == serverId &&
                entry.Type == GameEventType.RoundEnded)
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(limit)
            .Select(entry => new GameEventHistoryItemDto(
                entry.Type,
                entry.OccurredAtUtc,
                entry.Map,
                entry.Players,
                entry.Bots))
            .ToListAsync(cancellationToken);
    }
}
