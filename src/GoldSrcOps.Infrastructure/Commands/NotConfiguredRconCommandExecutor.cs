using GoldSrcOps.Application.Commands;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed class NotConfiguredRconCommandExecutor : IRconCommandExecutor
{
    public Task<RconCommandExecutionResult> ExecuteAsync(
        RconCommandExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(RconCommandExecutionResult.Failed("RCON executor is not configured."));
    }
}
