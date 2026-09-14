using System.Diagnostics;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;

namespace GoldSrcOps.Application.GameEvents;

public sealed class GameEventRetentionService
{
    private readonly IGameEventInboxRetentionRepository _inbox;
    private readonly IClock _clock;
    private readonly GameEventRetentionSettings _settings;

    public GameEventRetentionService(
        IGameEventInboxRetentionRepository inbox,
        IClock clock,
        GameEventRetentionSettings settings)
    {
        _inbox = inbox;
        _clock = clock;
        _settings = settings;
    }

    public async Task<GameEventRetentionResult> CleanupAsync(CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var cutoffUtc = _clock.UtcNow - _settings.RetentionPeriod;

        try
        {
            var deletedEvents = await _inbox.DeleteBatchReceivedBeforeAsync(
                cutoffUtc,
                _settings.BatchSize,
                cancellationToken);
            var duration = Stopwatch.GetElapsedTime(startedAt);

            GoldSrcOpsMetrics.RecordGameEventRetentionCompleted(deletedEvents, duration);

            return new GameEventRetentionResult(
                cutoffUtc,
                deletedEvents,
                BatchLimitReached: deletedEvents == _settings.BatchSize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            GoldSrcOpsMetrics.RecordGameEventRetentionFailed(Stopwatch.GetElapsedTime(startedAt));
            throw;
        }
    }
}
