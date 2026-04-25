using GoldSrcOps.Application.Servers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed partial class GoldSrcPollingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GoldSrcPollingOptions _options;
    private readonly ILogger<GoldSrcPollingBackgroundService> _logger;

    public GoldSrcPollingBackgroundService(
        IServiceScopeFactory scopeFactory,
        GoldSrcPollingOptions options,
        ILogger<GoldSrcPollingBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogPollingServiceStarted(_logger, _options.LoopDelay, _options.QueryTimeout);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPollingPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogPollingPassFailed(_logger, ex);
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

    private async Task RunPollingPassAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var pollingService = scope.ServiceProvider.GetRequiredService<ServerPollingService>();
        var result = await pollingService.PollDueServersAsync(stoppingToken);

        if (result.DueServers > 0)
        {
            LogPollingPassCompleted(
                _logger,
                result.SuccessfulPolls,
                result.FailedPolls,
                result.OpenedIncidents,
                result.ClosedIncidents);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "GoldSrc polling service started with {LoopDelay} loop delay and {QueryTimeout} query timeout.")]
    private static partial void LogPollingServiceStarted(
        ILogger logger,
        TimeSpan loopDelay,
        TimeSpan queryTimeout);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "GoldSrc polling pass failed.")]
    private static partial void LogPollingPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "GoldSrc polling pass completed: {SuccessfulPolls} succeeded, {FailedPolls} failed, {OpenedIncidents} incidents opened, {ClosedIncidents} incidents closed.")]
    private static partial void LogPollingPassCompleted(
        ILogger logger,
        int successfulPolls,
        int failedPolls,
        int openedIncidents,
        int closedIncidents);
}
