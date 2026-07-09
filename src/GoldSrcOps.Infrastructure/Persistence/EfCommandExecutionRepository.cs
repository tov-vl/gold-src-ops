using GoldSrcOps.Application.Commands;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfCommandExecutionRepository : ICommandExecutionRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfCommandExecutionRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .AnyAsync(x => x.Id == serverId, cancellationToken);
    }

    public async Task<bool> HasCredentialAsync(
        Guid serverId,
        ServerCredentialKind kind,
        CancellationToken cancellationToken)
    {
        return await _dbContext.ServerCredentials
            .AsNoTracking()
            .AnyAsync(x => x.ServerId == serverId && x.Kind == kind, cancellationToken);
    }

    public async Task AddAsync(CommandExecution command, CancellationToken cancellationToken)
    {
        await _dbContext.CommandExecutions.AddAsync(command, cancellationToken);
    }

    public async Task<CommandExecution?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _dbContext.CommandExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<CommandExecution>> ListByServerAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken)
    {
        return await _dbContext.CommandExecutions
            .AsNoTracking()
            .Where(x => x.ServerId == serverId)
            .OrderByDescending(x => x.RequestedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }
}
