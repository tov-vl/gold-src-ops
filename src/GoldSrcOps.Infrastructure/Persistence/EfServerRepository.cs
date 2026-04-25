using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfServerRepository : IServerRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfServerRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Server server, CancellationToken cancellationToken)
    {
        await _dbContext.Servers.AddAsync(server, cancellationToken);
    }

    public async Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .Include(x => x.CurrentState)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Server>> ListEnabledAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .Include(x => x.CurrentState)
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddSnapshotAsync(PollSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _dbContext.PollSnapshots.AddAsync(snapshot, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }
}
