using System.Diagnostics;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Infrastructure.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed partial class PublicLeaderboardBackgroundService(
    IServiceScopeFactory scopeFactory,
    PublicLeaderboardSettings settings,
    PublicLeaderboardStore store,
    PublicLeaderboardMetrics metrics,
    ILogger<PublicLeaderboardBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            PublicLeaderboardPollResult result;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                result = await scope.ServiceProvider.GetRequiredService<PublicLeaderboardPoller>().PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                result = FailureResult(exception);
                store.RecordFailure();
                LogPollFailed(logger);
            }

            metrics.RecordAttempt(result, Stopwatch.GetElapsedTime(started));
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static PublicLeaderboardPollResult FailureResult(Exception exception) => exception switch
    {
        TimeoutException => PublicLeaderboardPollResult.Timeout,
        GoldSrcRconAuthenticationException => PublicLeaderboardPollResult.AuthenticationFailed,
        InvalidDataException or GoldSrcRconProtocolException => PublicLeaderboardPollResult.InvalidFrame,
        _ => PublicLeaderboardPollResult.Failed
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Public leaderboard polling failed.")]
    private static partial void LogPollFailed(ILogger logger);
}
