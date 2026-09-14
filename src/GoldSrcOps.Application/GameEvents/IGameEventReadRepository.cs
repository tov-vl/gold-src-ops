namespace GoldSrcOps.Application.GameEvents;

public interface IGameEventReadRepository
{
    Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GameEventHistoryItemDto>> ListRecentRoundEndedAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken);
}
