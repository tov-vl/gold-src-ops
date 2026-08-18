using GoldSrcOps.Application.Commands;
using GoldSrcOps.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed partial class CommandDispatchBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly CommandDispatcherOptions _options;
    private readonly ILogger<CommandDispatchBackgroundService> _logger;

    public CommandDispatchBackgroundService(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        CommandDispatcherOptions options,
        ILogger<CommandDispatchBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogDispatcherStarted(
            _logger,
            _options.MaxConcurrency,
            _options.LoopDelay,
            _options.InterruptedAfter);

        var nextRecoveryAtUtc = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = _clock.UtcNow;
                if (now >= nextRecoveryAtUtc)
                {
                    await RecoverInterruptedAsync(stoppingToken);
                    nextRecoveryAtUtc = now + _options.RecoveryInterval;
                }

                var claimed = await DispatchBatchAsync(stoppingToken);
                if (claimed > 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogDispatcherPassFailed(_logger, exception);
            }

            try
            {
                await Task.Delay(_options.LoopDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CommandDispatcher>();
        var recovered = await dispatcher.RecoverInterruptedAsync(_options.InterruptedAfter, cancellationToken);

        if (recovered > 0)
        {
            LogInterruptedCommandsRecovered(_logger, recovered);
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var tasks = Enumerable.Range(0, _options.MaxConcurrency)
            .Select(_ => DispatchOneAsync(cancellationToken));
        var results = await Task.WhenAll(tasks);

        return results.Count(static result => result.Kind != CommandDispatchAttemptResultKind.NoCommand);
    }

    private async Task<CommandDispatchAttemptResult> DispatchOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CommandDispatcher>();
        return await dispatcher.DispatchNextAsync(cancellationToken);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Command dispatcher started with concurrency {MaxConcurrency}, loop delay {LoopDelay}, and interrupted timeout {InterruptedAfter}.")]
    private static partial void LogDispatcherStarted(
        ILogger logger,
        int maxConcurrency,
        TimeSpan loopDelay,
        TimeSpan interruptedAfter);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Recovered {RecoveredCount} interrupted command executions as failed.")]
    private static partial void LogInterruptedCommandsRecovered(ILogger logger, int recoveredCount);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "Command dispatcher pass failed.")]
    private static partial void LogDispatcherPassFailed(ILogger logger, Exception exception);

}
