using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal interface IArchiveHealthClient
{
    Task<ArchiveHealthResponse> ReadAsync(CancellationToken cancellationToken = default);
}
