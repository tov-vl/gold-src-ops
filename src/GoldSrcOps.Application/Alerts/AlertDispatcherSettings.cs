namespace GoldSrcOps.Application.Alerts;

public sealed record AlertDispatcherSettings
{
    public AlertDispatcherSettings(
        TimeSpan claimTimeout,
        int maxAttempts,
        TimeSpan baseRetryDelay,
        TimeSpan maximumRetryDelay,
        TimeSpan processedRetentionPeriod,
        int cleanupBatchSize)
    {
        if (claimTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(claimTimeout),
                claimTimeout,
                "Claim timeout must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        if (baseRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseRetryDelay),
                baseRetryDelay,
                "Base retry delay must be positive.");
        }

        if (maximumRetryDelay < baseRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetryDelay),
                maximumRetryDelay,
                "Maximum retry delay must not be shorter than the base retry delay.");
        }

        if (processedRetentionPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processedRetentionPeriod),
                processedRetentionPeriod,
                "Processed-message retention period must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cleanupBatchSize);

        ClaimTimeout = claimTimeout;
        MaxAttempts = maxAttempts;
        BaseRetryDelay = baseRetryDelay;
        MaximumRetryDelay = maximumRetryDelay;
        ProcessedRetentionPeriod = processedRetentionPeriod;
        CleanupBatchSize = cleanupBatchSize;
    }

    public TimeSpan ClaimTimeout { get; }

    public int MaxAttempts { get; }

    public TimeSpan BaseRetryDelay { get; }

    public TimeSpan MaximumRetryDelay { get; }

    public TimeSpan ProcessedRetentionPeriod { get; }

    public int CleanupBatchSize { get; }
}
