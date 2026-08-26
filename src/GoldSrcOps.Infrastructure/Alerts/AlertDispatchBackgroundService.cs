using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Infrastructure.Alerts;

internal sealed partial class AlertDispatchBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly AlertDeliveryOptions _options;
    private readonly ILogger<AlertDispatchBackgroundService> _logger;

    public AlertDispatchBackgroundService(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        AlertDeliveryOptions options,
        ILogger<AlertDispatchBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(
            _logger,
            _options.MaxConcurrency,
            _options.LoopDelay,
            _options.ClaimTimeout,
            _options.MaxAttempts,
            _options.ProcessedRetentionPeriod,
            _options.CleanupBatchSize);

        var nextRecoveryAtUtc = DateTimeOffset.MinValue;
        var nextMetricsAtUtc = DateTimeOffset.MinValue;
        var nextCleanupAtUtc = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = _clock.UtcNow;
                if (nowUtc >= nextRecoveryAtUtc)
                {
                    nextRecoveryAtUtc = nowUtc + _options.RecoveryInterval;
                    await RunMaintenanceAsync(
                        "claim_recovery",
                        RecoverExpiredClaimsAsync,
                        stoppingToken);
                }

                if (nowUtc >= nextMetricsAtUtc)
                {
                    nextMetricsAtUtc = nowUtc + _options.MetricsInterval;
                    await RunMaintenanceAsync(
                        "statistics_refresh",
                        RefreshStatisticsAsync,
                        stoppingToken);
                }

                if (nowUtc >= nextCleanupAtUtc)
                {
                    nextCleanupAtUtc = nowUtc + _options.CleanupInterval;
                    await RunMaintenanceAsync(
                        "processed_retention",
                        CleanupProcessedAsync,
                        stoppingToken);
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
                LogServicePassFailed(_logger, exception.GetType().Name);
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

    private async Task RunMaintenanceAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogMaintenanceFailed(
                _logger,
                operationName,
                exception.GetType().Name);
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var attempts = Enumerable.Range(0, _options.MaxConcurrency)
            .Select(_ => DispatchOneAsync(cancellationToken));
        var results = await Task.WhenAll(attempts);

        return results.Count(static result => result.Kind != AlertDispatchAttemptResultKind.NoMessage);
    }

    private async Task<AlertDispatchAttemptResult> DispatchOneAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<AlertDispatcher>();
        return await dispatcher.DispatchNextAsync(cancellationToken);
    }

    private async Task RecoverExpiredClaimsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<AlertDispatcher>();
        var recovered = await dispatcher.RecoverExpiredClaimsAsync(cancellationToken);

        if (recovered.TotalRecovered > 0)
        {
            LogClaimsRecovered(
                _logger,
                recovered.RetryScheduled,
                recovered.DeadLettered);
        }
    }

    private async Task RefreshStatisticsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<AlertDispatcher>();
        await dispatcher.RefreshStatisticsAsync(cancellationToken);
    }

    private async Task CleanupProcessedAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<AlertDispatcher>();
        var result = await dispatcher.CleanupProcessedAsync(cancellationToken);

        if (result.DeletedMessages > 0)
        {
            LogCleanupCompleted(
                _logger,
                result.DeletedMessages,
                result.CutoffUtc,
                result.BatchLimitReached);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Alert delivery service started with concurrency {MaxConcurrency}, loop delay {LoopDelay}, claim timeout {ClaimTimeout}, maximum attempts {MaxAttempts}, processed retention {ProcessedRetentionPeriod}, and cleanup batch size {CleanupBatchSize}.")]
    private static partial void LogServiceStarted(
        ILogger logger,
        int maxConcurrency,
        TimeSpan loopDelay,
        TimeSpan claimTimeout,
        int maxAttempts,
        TimeSpan processedRetentionPeriod,
        int cleanupBatchSize);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Recovered expired alert delivery claims: {RetryScheduledCount} scheduled for retry and {DeadLetteredCount} moved to dead letter.")]
    private static partial void LogClaimsRecovered(
        ILogger logger,
        int retryScheduledCount,
        int deadLetteredCount);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Alert outbox retention deleted {DeletedMessages} processed messages older than {CutoffUtc}; batch limit reached: {BatchLimitReached}.")]
    private static partial void LogCleanupCompleted(
        ILogger logger,
        int deletedMessages,
        DateTimeOffset cutoffUtc,
        bool batchLimitReached);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Alert delivery service pass failed with failure type {FailureType}.")]
    private static partial void LogServicePassFailed(ILogger logger, string failureType);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Error,
        Message = "Alert delivery maintenance operation {OperationName} failed with failure type {FailureType}.")]
    private static partial void LogMaintenanceFailed(
        ILogger logger,
        string operationName,
        string failureType);
}
