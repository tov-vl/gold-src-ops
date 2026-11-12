using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal interface IPollingHealthClient
{
    Task<PollingHealthResponse> ReadAsync(CancellationToken cancellationToken = default);
}
