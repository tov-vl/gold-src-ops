using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Alerts;

internal sealed class AlertDeliveryOptions
{
    private const string SectionName = "AlertDelivery";

    public bool Enabled { get; init; }

    public Uri? WebhookEndpoint { get; init; }

    public string? Authorization { get; init; }

    public TimeSpan LoopDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public int MaxConcurrency { get; init; } = 4;

    public TimeSpan ClaimTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxAttempts { get; init; } = 8;

    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MetricsInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ProcessedRetentionPeriod { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);

    public int CleanupBatchSize { get; init; } = 1_000;

    public static AlertDeliveryOptions FromConfiguration(
        IConfiguration configuration,
        bool allowHttpEndpoint)
    {
        var section = configuration.GetSection(SectionName);
        var options = new AlertDeliveryOptions
        {
            Enabled = ReadBool(section, "Enabled", defaultValue: false),
            WebhookEndpoint = ReadEndpoint(section, allowHttpEndpoint),
            Authorization = NormalizeOptional(section["Authorization"]),
            LoopDelay = TimeSpan.FromMilliseconds(
                ReadInt(section, "LoopDelayMilliseconds", 500, 10, 60_000)),
            MaxConcurrency = ReadInt(section, "MaxConcurrency", 4, 1, 32),
            ClaimTimeout = TimeSpan.FromSeconds(
                ReadInt(section, "ClaimTimeoutSeconds", 30, 2, 86_400)),
            RecoveryInterval = TimeSpan.FromSeconds(
                ReadInt(section, "RecoveryIntervalSeconds", 30, 1, 3_600)),
            RequestTimeout = TimeSpan.FromSeconds(
                ReadInt(section, "RequestTimeoutSeconds", 10, 1, 300)),
            MaxAttempts = ReadInt(section, "MaxAttempts", 8, 1, 100),
            BaseRetryDelay = TimeSpan.FromSeconds(
                ReadInt(section, "BaseRetryDelaySeconds", 5, 1, 3_600)),
            MaximumRetryDelay = TimeSpan.FromSeconds(
                ReadInt(section, "MaximumRetryDelaySeconds", 300, 1, 86_400)),
            MetricsInterval = TimeSpan.FromSeconds(
                ReadInt(section, "MetricsIntervalSeconds", 30, 1, 3_600)),
            ProcessedRetentionPeriod = TimeSpan.FromDays(
                ReadInt(section, "ProcessedRetentionDays", 30, 1, 3_650)),
            CleanupInterval = TimeSpan.FromSeconds(
                ReadInt(section, "CleanupIntervalSeconds", 300, 10, 86_400)),
            CleanupBatchSize = ReadInt(section, "CleanupBatchSize", 1_000, 1, 10_000)
        };

        if (options.Enabled && options.WebhookEndpoint is null)
        {
            throw InvalidValue("WebhookUrl", "an absolute webhook URL when alert delivery is enabled");
        }

        if (options.ClaimTimeout <= options.RequestTimeout)
        {
            throw new InvalidOperationException(
                "Configuration value 'AlertDelivery:ClaimTimeoutSeconds' must exceed 'AlertDelivery:RequestTimeoutSeconds'.");
        }

        if (options.MaximumRetryDelay < options.BaseRetryDelay)
        {
            throw new InvalidOperationException(
                "Configuration value 'AlertDelivery:MaximumRetryDelaySeconds' must not be less than 'AlertDelivery:BaseRetryDelaySeconds'.");
        }

        return options;
    }

    private static Uri? ReadEndpoint(IConfiguration section, bool allowHttpEndpoint)
    {
        var rawEndpoint = section["WebhookUrl"];
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            return null;
        }

        if (!Uri.TryCreate(rawEndpoint.Trim(), UriKind.Absolute, out var endpoint) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            (!endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !(allowHttpEndpoint && endpoint.Scheme.Equals(
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase))))
        {
            throw InvalidValue(
                "WebhookUrl",
                allowHttpEndpoint
                    ? "an absolute HTTP or HTTPS URL without user information"
                    : "an absolute HTTPS URL without user information");
        }

        return endpoint;
    }

    private static bool ReadBool(IConfiguration section, string key, bool defaultValue)
    {
        var value = section[key];
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw InvalidValue(key, "a Boolean value");
    }

    private static int ReadInt(
        IConfiguration section,
        string key,
        int defaultValue,
        int minValue,
        int maxValue)
    {
        var value = section[key];
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= minValue &&
            parsed <= maxValue
                ? parsed
                : throw InvalidValue(
                    key,
                    $"an integer between {minValue.ToString(CultureInfo.InvariantCulture)} and {maxValue.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static InvalidOperationException InvalidValue(string key, string expected) =>
        new($"Configuration value '{SectionName}:{key}' must be {expected}.");
}
