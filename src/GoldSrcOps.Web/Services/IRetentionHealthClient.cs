using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal interface IRetentionHealthClient
{
    Task<RetentionHealthResponse> ReadAsync(CancellationToken cancellationToken = default);
}
