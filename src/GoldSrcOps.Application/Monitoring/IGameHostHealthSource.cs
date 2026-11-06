namespace GoldSrcOps.Application.Monitoring;

public interface IGameHostHealthSource
{
    Task<IReadOnlyList<GameHostHealthSample>?> ReadAsync(DateTimeOffset atUtc, CancellationToken cancellationToken);
}
