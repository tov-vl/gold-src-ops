namespace GoldSrcOps.Application.Monitoring;

public interface ICommandDispatchHealthSource
{
    Task<CommandDispatchHealthSnapshot> ReadAsync(DateTimeOffset atUtc, CancellationToken cancellationToken);
}
