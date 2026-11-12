using GoldSrcOps.Application.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed partial class SnapshotRetentionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SnapshotRetentionOptions _options;
    private readonly ILogger<SnapshotRetentionBackgroundService> _logger;
    private readonly SnapshotRetentionMetrics _metrics;

    public SnapshotRetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        SnapshotRetentionOptions options,
        ILogger<SnapshotRetentionBackgroundService> logger,
        SnapshotRetentionMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(
            _logger,
            _options.RetentionPeriod,
            _options.CleanupInterval,
            _options.BatchSize);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _metrics.BeginCycle();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    await RunCleanupPassAsync(stoppingToken).ConfigureAwait(false);
                    _metrics.CompleteCycle(failed: false, System.Diagnostics.Stopwatch.GetElapsedTime(started));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _metrics.CompleteCycle(failed: true, System.Diagnostics.Stopwatch.GetElapsedTime(started));
                    LogCleanupPassFailed(_logger, ex);
                }

                try
                {
                    await Task.Delay(_options.CleanupInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _metrics.WorkerStopped();
        }
    }

    private async Task RunCleanupPassAsync(CancellationToken stoppingToken)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var retentionService = scope.ServiceProvider.GetRequiredService<SnapshotRetentionService>();
            var result = await retentionService.CleanupAsync(stoppingToken).ConfigureAwait(false);

            if (result.DeletedSnapshots > 0)
            {
                LogCleanupPassCompleted(
                    _logger,
                    result.DeletedSnapshots,
                    result.CutoffUtc,
                    result.BatchLimitReached);
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Snapshot retention service started with {RetentionPeriod} retention, {CleanupInterval} cleanup interval, and batch size {BatchSize}.")]
    private static partial void LogServiceStarted(
        ILogger logger,
        TimeSpan retentionPeriod,
        TimeSpan cleanupInterval,
        int batchSize);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Snapshot retention cleanup pass failed.")]
    private static partial void LogCleanupPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Snapshot retention cleanup pass deleted {DeletedSnapshots} snapshots older than {CutoffUtc}; batch limit reached: {BatchLimitReached}.")]
    private static partial void LogCleanupPassCompleted(
        ILogger logger,
        int deletedSnapshots,
        DateTimeOffset cutoffUtc,
        bool batchLimitReached);
}
