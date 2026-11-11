namespace GoldSrcOps.Application.Monitoring;

public interface IArchiveHealthSource
{
    Task<ArchiveHealthSnapshot> ReadAsync(DateTimeOffset atUtc, CancellationToken cancellationToken);
}
