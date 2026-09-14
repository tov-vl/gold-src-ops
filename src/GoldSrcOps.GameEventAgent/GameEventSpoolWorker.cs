using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.GameEventAgent;

internal sealed class GameEventSpoolWorker(
    GameEventSpoolImporter importer,
    GameEventSpoolOptions options,
    TimeProvider timeProvider,
    ILogger<GameEventSpoolWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await importer.ImportBatchAsync(stoppingToken).ConfigureAwait(false);
            if (result.Processed > 0)
            {
                GameEventSpoolWorkerLog.BatchProcessed(
                    logger,
                    result.Imported,
                    result.Reconciled,
                    result.Finalized,
                    result.Rejected,
                    result.Deferred);
            }

            await Task.Delay(options.ImportInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static partial class GameEventSpoolWorkerLog
{
    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "Game-event spool batch completed: imported={Imported}, reconciled={Reconciled}, finalized={Finalized}, rejected={Rejected}, deferred={Deferred}.")]
    public static partial void BatchProcessed(
        ILogger logger,
        int imported,
        int reconciled,
        int finalized,
        int rejected,
        int deferred);
}
