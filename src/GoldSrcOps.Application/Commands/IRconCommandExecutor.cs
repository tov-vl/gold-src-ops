namespace GoldSrcOps.Application.Commands;

public interface IRconCommandExecutor
{
    Task<RconCommandExecutionResult> ExecuteAsync(
        RconCommandExecutionRequest request,
        CancellationToken cancellationToken);
}
