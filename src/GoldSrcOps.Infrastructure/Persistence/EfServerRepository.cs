using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfServerRepository : IServerRepository
{
    internal const string RegistrationRequestIdIndex = "ux_servers_registration_request_id";

    private readonly GoldSrcOpsDbContext _dbContext;

    public EfServerRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServerRegistrationPersistenceResult> RegisterAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        if (server.RegistrationRequestId is { } requestId &&
            await FindRegistrationAsync(requestId, cancellationToken) is { } existing)
        {
            return new ServerRegistrationPersistenceResult(WasCreated: false, existing);
        }

        await _dbContext.Servers.AddAsync(server, cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return new ServerRegistrationPersistenceResult(WasCreated: true, server);
        }
        catch (DbUpdateException exception) when (
            server.RegistrationRequestId is { } duplicateRequestId &&
            IsRegistrationRequestIdViolation(exception))
        {
            _dbContext.ChangeTracker.Clear();
            existing = await FindRegistrationAsync(duplicateRequestId, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return new ServerRegistrationPersistenceResult(WasCreated: false, existing);
        }
    }

    public async Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .Include(x => x.CurrentState)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<Server?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
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
    }

    private async Task<Server?> FindRegistrationAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        await _dbContext.Servers
            .AsNoTracking()
            .Include(server => server.CurrentState)
            .SingleOrDefaultAsync(
                server => server.RegistrationRequestId == requestId,
                cancellationToken);

    private static bool IsRegistrationRequestIdViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: RegistrationRequestIdIndex
        };
}
