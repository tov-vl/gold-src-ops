using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfServerCredentialRepository : IServerCredentialRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfServerCredentialRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .AnyAsync(x => x.Id == serverId, cancellationToken);
    }

    public async Task AddAsync(ServerCredential credential, CancellationToken cancellationToken)
    {
        await _dbContext.ServerCredentials.AddAsync(credential, cancellationToken);
    }

    public async Task<ServerCredential?> GetAsync(
        Guid serverId,
        ServerCredentialKind kind,
        CancellationToken cancellationToken)
    {
        return await _dbContext.ServerCredentials
            .FirstOrDefaultAsync(x => x.ServerId == serverId && x.Kind == kind, cancellationToken);
    }

    public async Task<IReadOnlyList<ServerCredential>> ListByServerAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.ServerCredentials
            .AsNoTracking()
            .Where(x => x.ServerId == serverId)
            .OrderBy(x => x.Kind)
            .ToListAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }
}
