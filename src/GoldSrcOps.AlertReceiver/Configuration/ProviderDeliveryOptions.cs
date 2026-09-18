namespace GoldSrcOps.AlertReceiver.Configuration;

internal sealed class ProviderDeliveryOptions
{
    public const string SectionName = "ProviderDelivery";

    public bool Enabled { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    public string Authorization { get; init; } = string.Empty;

    public int MaxConcurrency { get; init; } = 2;

    public int MaxAttempts { get; init; } = 5;

    public int CleanupBatchSize { get; init; } = 100;

    public TimeSpan LoopDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ClaimTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MetricsInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaximumRetryAfter { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan ProcessedRetentionPeriod { get; init; } = TimeSpan.FromDays(7);

    public bool IsValid(string environmentName)
    {
        if (MaxConcurrency is <= 0 or > 16 || MaxAttempts is <= 0 or > 20 ||
            CleanupBatchSize is <= 0 or > 1000 ||
            LoopDelay <= TimeSpan.Zero || LoopDelay > TimeSpan.FromMinutes(1) ||
            ClaimTimeout <= TimeSpan.Zero || ClaimTimeout > TimeSpan.FromHours(1) ||
            RecoveryInterval <= TimeSpan.Zero || MetricsInterval <= TimeSpan.Zero ||
            CleanupInterval <= TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero ||
            RequestTimeout > TimeSpan.FromMinutes(1) ||
            BaseRetryDelay <= TimeSpan.Zero || MaximumRetryDelay < BaseRetryDelay ||
            MaximumRetryAfter < TimeSpan.Zero || ProcessedRetentionPeriod <= TimeSpan.Zero ||
            ProcessedRetentionPeriod > TimeSpan.FromDays(90))
        {
            return false;
        }

        if (!Enabled)
        {
            return true;
        }

        if (Endpoint.Length > 2048 || Authorization.Length > 8192 ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            string.IsNullOrWhiteSpace(Authorization))
        {
            return false;
        }

        return string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(environmentName, "Testing", StringComparison.Ordinal) &&
                string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
    }
}
