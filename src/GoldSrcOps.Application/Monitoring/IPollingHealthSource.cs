namespace GoldSrcOps.Application.Monitoring;

public interface IPollingHealthSource
{
    Task<PollingHealthSnapshot> ReadAsync(DateTimeOffset atUtc, CancellationToken cancellationToken);
}
