namespace GoldSrcOps.Application.Monitoring;

public interface IRetentionHealthSource
{
    Task<RetentionHealthSnapshot> ReadAsync(DateTimeOffset atUtc, CancellationToken cancellationToken);
}
