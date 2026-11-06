using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal interface IGameHostHealthClient
{
    Task<GameHostHealthResponse> ReadAsync(CancellationToken cancellationToken = default);
}
