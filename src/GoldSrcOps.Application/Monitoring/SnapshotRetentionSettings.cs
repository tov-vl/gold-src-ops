namespace GoldSrcOps.Application.Monitoring;

public sealed record SnapshotRetentionSettings
{
    public SnapshotRetentionSettings(TimeSpan retentionPeriod, int batchSize)
    {
        if (retentionPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionPeriod),
                retentionPeriod,
                "Snapshot retention period must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        RetentionPeriod = retentionPeriod;
        BatchSize = batchSize;
    }

    public TimeSpan RetentionPeriod { get; }

    public int BatchSize { get; }
}
