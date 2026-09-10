using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfServerCredentialRepository : IServerCredentialRepository
{
    internal const string ServerKindIndex = "ux_server_credentials_server_id_kind";

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

    public async Task<Server?> GetServerForUpdateAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .FirstOrDefaultAsync(x => x.Id == serverId, cancellationToken);
    }

    public async Task<bool> HasIncompleteCommandsAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.CommandExecutions
            .AsNoTracking()
            .AnyAsync(
                x => x.ServerId == serverId &&
                    (x.Status == CommandExecutionStatus.Pending ||
                     x.Status == CommandExecutionStatus.Running),
                cancellationToken);
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

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException exception) when (IsServerKindViolation(exception))
        {
            return false;
        }
    }

    private static bool IsServerKindViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ServerKindIndex
        };
}
