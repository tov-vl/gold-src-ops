using GoldSrcOps.AlertReceiver.Configuration;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.AlertReceiver.ProviderDelivery;

internal sealed partial class ProviderDeliveryBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<ProviderDeliveryOptions> options,
    ILogger<ProviderDeliveryBackgroundService> logger) : BackgroundService
{
    private readonly ProviderDeliveryOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        LogStarted(logger, _options.MaxConcurrency, _options.MaxAttempts);
        var nextRecovery = DateTimeOffset.MinValue;
        var nextMetrics = DateTimeOffset.MinValue;
        var nextCleanup = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                if (now >= nextRecovery)
                {
                    nextRecovery = now + _options.RecoveryInterval;
                    await RunMaintenanceAsync(
                        static (dispatcher, token) => dispatcher.RecoverExpiredClaimsAsync(token),
                        stoppingToken);
                }

                if (now >= nextMetrics)
                {
                    nextMetrics = now + _options.MetricsInterval;
                    await RunMaintenanceAsync(
                        static (dispatcher, token) => dispatcher.RefreshStatisticsAsync(token),
                        stoppingToken);
                }

                if (now >= nextCleanup)
                {
                    nextCleanup = now + _options.CleanupInterval;
                    await RunMaintenanceAsync(
                        static (dispatcher, token) => dispatcher.CleanupProcessedAsync(token),
                        stoppingToken);
                }

                var attempts = Enumerable.Range(0, _options.MaxConcurrency)
                    .Select(_ => DispatchOneAsync(stoppingToken));
                var results = await Task.WhenAll(attempts);
                if (results.Any(static result => result != ProviderDispatchResult.NoMessage))
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
                LogPassFailed(logger, exception.GetType().Name);
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

    private async Task<ProviderDispatchResult> DispatchOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ProviderDispatcher>();
        return await dispatcher.DispatchNextAsync(cancellationToken);
    }

    private async Task RunMaintenanceAsync<T>(
        Func<ProviderDispatcher, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ProviderDispatcher>();
        await operation(dispatcher, cancellationToken);
    }

    private async Task RunMaintenanceAsync(
        Func<ProviderDispatcher, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ProviderDispatcher>();
        await operation(dispatcher, cancellationToken);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Provider delivery worker is disabled.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Provider delivery worker started with concurrency {MaxConcurrency} and maximum attempts {MaxAttempts}.")]
    private static partial void LogStarted(ILogger logger, int maxConcurrency, int maxAttempts);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Provider delivery worker pass failed with failure type {FailureType}.")]
    private static partial void LogPassFailed(ILogger logger, string failureType);
}
