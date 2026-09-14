using GoldSrcOps.Domain.GameEvents;

namespace GoldSrcOps.Application.GameEvents;

public enum GameEventInboxPersistenceResultKind
{
    Created = 1,
    EventIdExists = 2,
    SourceSequenceExists = 3
}

public sealed record GameEventInboxPersistenceResult(
    GameEventInboxPersistenceResultKind Kind,
    GameEventInboxEntry Entry);

public interface IGameEventInboxRepository
{
    Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken);

    Task<GameEventInboxPersistenceResult> StoreAsync(
        GameEventInboxEntry entry,
        CancellationToken cancellationToken);
}
