namespace GoldSrcOps.GameEventAgent;

internal sealed class GameEventSpoolWorker(
    GameEventSpoolImporter importer,
    GameEventSpoolOptions options,
    TimeProvider timeProvider,
    GameEventAgentHealth health,
    ILogger<GameEventSpoolWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                health.RecordStart(import: true);
                GameEventSpoolImportBatchResult result;
                try
                {
                    result = await importer.ImportBatchAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    health.RecordCompletion(import: true, "failed");
                    throw;
                }

                health.RecordCompletion(import: true, result.Deferred > 0 || result.Rejected > 0
                    ? "blocked" : result.Processed == 0 ? "idle" : "progress");
                if (result.Processed > 0)
                {
                    GameEventSpoolWorkerLog.BatchProcessed(logger, result.Imported, result.Reconciled,
                        result.Finalized, result.Rejected, result.Deferred);
                }

                await Task.Delay(options.ImportInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            health.RecordStop(import: true);
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
