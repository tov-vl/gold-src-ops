using GoldSrcOps.Application.GameEvents;
using GoldSrcOps.Domain.GameEvents;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GoldSrcOps.Infrastructure.Persistence.GameEvents;

internal sealed class EfGameEventInboxRepository : IGameEventInboxRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfGameEventInboxRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .AnyAsync(server => server.Id == serverId, cancellationToken);
    }

    public async Task<GameEventInboxPersistenceResult> StoreAsync(
        GameEventInboxEntry entry,
        CancellationToken cancellationToken)
    {
        var existing = await FindByIdAsync(entry.Id, cancellationToken);
        if (existing is not null)
        {
            return new GameEventInboxPersistenceResult(
                GameEventInboxPersistenceResultKind.EventIdExists,
                existing);
        }

        existing = await FindBySourceSequenceAsync(entry, cancellationToken);
        if (existing is not null)
        {
            return new GameEventInboxPersistenceResult(
                GameEventInboxPersistenceResultKind.SourceSequenceExists,
                existing);
        }

        await _dbContext.GameEventInbox.AddAsync(entry, cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return new GameEventInboxPersistenceResult(
                GameEventInboxPersistenceResultKind.Created,
                entry);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _dbContext.ChangeTracker.Clear();

            existing = await FindByIdAsync(entry.Id, cancellationToken);
            if (existing is not null)
            {
                return new GameEventInboxPersistenceResult(
                    GameEventInboxPersistenceResultKind.EventIdExists,
                    existing);
            }

            existing = await FindBySourceSequenceAsync(entry, cancellationToken);
            if (existing is not null)
            {
                return new GameEventInboxPersistenceResult(
                    GameEventInboxPersistenceResultKind.SourceSequenceExists,
                    existing);
            }

            throw;
        }
    }

    private async Task<GameEventInboxEntry?> FindByIdAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        await _dbContext.GameEventInbox
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Id == id, cancellationToken);

    private async Task<GameEventInboxEntry?> FindBySourceSequenceAsync(
        GameEventInboxEntry entry,
        CancellationToken cancellationToken) =>
        await _dbContext.GameEventInbox
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.ServerId == entry.ServerId &&
                    candidate.SourceInstanceId == entry.SourceInstanceId &&
                    candidate.SequenceNumber == entry.SequenceNumber,
                cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };
}
