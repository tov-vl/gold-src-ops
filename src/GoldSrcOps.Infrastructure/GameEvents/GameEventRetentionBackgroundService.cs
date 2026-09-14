using GoldSrcOps.Application.GameEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Infrastructure.GameEvents;

internal sealed partial class GameEventRetentionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GameEventRetentionOptions _options;
    private readonly ILogger<GameEventRetentionBackgroundService> _logger;

    public GameEventRetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        GameEventRetentionOptions options,
        ILogger<GameEventRetentionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(
            _logger,
            _options.RetentionPeriod,
            _options.CleanupInterval,
            _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogCleanupPassFailed(_logger, exception);
            }

            try
            {
                await Task.Delay(_options.CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunCleanupPassAsync(CancellationToken stoppingToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var retentionService = scope.ServiceProvider.GetRequiredService<GameEventRetentionService>();
        var result = await retentionService.CleanupAsync(stoppingToken);

        if (result.DeletedEvents > 0)
        {
            LogCleanupPassCompleted(
                _logger,
                result.DeletedEvents,
                result.CutoffUtc,
                result.BatchLimitReached);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Game event retention service started with {RetentionPeriod} retention, {CleanupInterval} cleanup interval, and batch size {BatchSize}.")]
    private static partial void LogServiceStarted(
        ILogger logger,
        TimeSpan retentionPeriod,
        TimeSpan cleanupInterval,
        int batchSize);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Game event retention cleanup pass failed.")]
    private static partial void LogCleanupPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Game event retention cleanup pass deleted {DeletedEvents} entries older than {CutoffUtc}; batch limit reached: {BatchLimitReached}.")]
    private static partial void LogCleanupPassCompleted(
        ILogger logger,
        int deletedEvents,
        DateTimeOffset cutoffUtc,
        bool batchLimitReached);
}
