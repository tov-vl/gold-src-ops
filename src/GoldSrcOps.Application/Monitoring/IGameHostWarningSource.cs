namespace GoldSrcOps.Application.Monitoring;

public interface IGameHostWarningSource
{
    Task<IReadOnlyList<GameHostWarningSample>?> ReadAsync(CancellationToken cancellationToken);
}
