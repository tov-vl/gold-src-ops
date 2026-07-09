using GoldSrcOps.Application.Commands;

namespace GoldSrcOps.UnitTests.Helpers;

internal sealed class CapturingRconCommandExecutor : IRconCommandExecutor
{
    private readonly Func<RconCommandExecutionRequest, CancellationToken, Task<RconCommandExecutionResult>> _execute;

    public CapturingRconCommandExecutor(RconCommandExecutionResult result)
        : this((_, _) => Task.FromResult(result))
    {
    }

    public CapturingRconCommandExecutor(
        Func<RconCommandExecutionRequest, CancellationToken, Task<RconCommandExecutionResult>> execute)
    {
        _execute = execute;
    }

    public RconCommandExecutionRequest? LastRequest { get; private set; }

    public int CallCount { get; private set; }

    public async Task<RconCommandExecutionResult> ExecuteAsync(
        RconCommandExecutionRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;

        return await _execute(request, cancellationToken);
    }
}
