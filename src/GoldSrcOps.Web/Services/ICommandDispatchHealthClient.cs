using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal interface ICommandDispatchHealthClient
{
    Task<CommandDispatchHealthResponse> ReadAsync(CancellationToken cancellationToken = default);
}
