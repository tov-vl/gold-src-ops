using GoldSrcOps.Application.Servers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed class GoldSrcPollingBackgroundService : BackgroundService
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
        _logger.LogInformation(
            "GoldSrc polling service started with {LoopDelaySeconds}s loop delay and {QueryTimeoutMilliseconds}ms query timeout.",
            _options.LoopDelay.TotalSeconds,
            _options.QueryTimeout.TotalMilliseconds);

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
                _logger.LogError(ex, "GoldSrc polling pass failed.");
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
            _logger.LogInformation(
                "GoldSrc polling pass completed: {SuccessfulPolls} succeeded, {FailedPolls} failed.",
                result.SuccessfulPolls,
                result.FailedPolls);
        }
    }
}
