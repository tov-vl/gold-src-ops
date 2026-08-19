using System.Diagnostics;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;

namespace GoldSrcOps.Application.Monitoring;

public sealed class SnapshotRetentionService
{
    private readonly IPollSnapshotRetentionRepository _snapshots;
    private readonly IClock _clock;
    private readonly SnapshotRetentionSettings _settings;

    public SnapshotRetentionService(
        IPollSnapshotRetentionRepository snapshots,
        IClock clock,
        SnapshotRetentionSettings settings)
    {
        _snapshots = snapshots;
        _clock = clock;
        _settings = settings;
    }

    public async Task<SnapshotRetentionResult> CleanupAsync(CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var cutoffUtc = _clock.UtcNow - _settings.RetentionPeriod;

        try
        {
            var deletedSnapshots = await _snapshots.DeleteBatchOlderThanAsync(
                cutoffUtc,
                _settings.BatchSize,
                cancellationToken);
            var duration = Stopwatch.GetElapsedTime(startedAt);

            GoldSrcOpsMetrics.RecordSnapshotRetentionCompleted(deletedSnapshots, duration);

            return new SnapshotRetentionResult(
                cutoffUtc,
                deletedSnapshots,
                BatchLimitReached: deletedSnapshots == _settings.BatchSize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            GoldSrcOpsMetrics.RecordSnapshotRetentionFailed(Stopwatch.GetElapsedTime(startedAt));
            throw;
        }
    }
}
