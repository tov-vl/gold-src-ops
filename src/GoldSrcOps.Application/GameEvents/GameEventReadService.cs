namespace GoldSrcOps.Application.GameEvents;

public sealed class GameEventReadService
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    private readonly IGameEventReadRepository _repository;

    public GameEventReadService(IGameEventReadRepository repository)
    {
        _repository = repository;
    }

    public async Task<GameEventHistoryDto?> ListRecentRoundsAsync(
        Guid serverId,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!await _repository.ServerExistsAsync(serverId, cancellationToken))
        {
            return null;
        }

        var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = await _repository.ListRecentRoundEndedAsync(
            serverId,
            effectiveLimit,
            cancellationToken);

        return new GameEventHistoryDto(serverId, effectiveLimit, items);
    }
}
