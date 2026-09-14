namespace GoldSrcOps.Application.GameEvents;

public sealed record GameEventRetentionSettings
{
    public GameEventRetentionSettings(TimeSpan retentionPeriod, int batchSize)
    {
        if (retentionPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionPeriod),
                retentionPeriod,
                "Game event retention period must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        RetentionPeriod = retentionPeriod;
        BatchSize = batchSize;
    }

    public TimeSpan RetentionPeriod { get; }

    public int BatchSize { get; }
}
