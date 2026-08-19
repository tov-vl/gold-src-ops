using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed class SnapshotRetentionOptions
{
    private const string SectionName = "SnapshotRetention";

    public bool Enabled { get; init; } = true;

    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);

    public int BatchSize { get; init; } = 1_000;

    public static SnapshotRetentionOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        return new SnapshotRetentionOptions
        {
            Enabled = ReadBool(section, "Enabled", defaultValue: true),
            RetentionPeriod = TimeSpan.FromDays(
                ReadInt(section, "RetentionDays", defaultValue: 30, minValue: 1, maxValue: 3_650)),
            CleanupInterval = TimeSpan.FromSeconds(
                ReadInt(section, "CleanupIntervalSeconds", defaultValue: 300, minValue: 10, maxValue: 86_400)),
            BatchSize = ReadInt(section, "BatchSize", defaultValue: 1_000, minValue: 1, maxValue: 10_000)
        };
    }

    private static bool ReadBool(IConfiguration section, string key, bool defaultValue)
    {
        var value = section[key];
        if (value is null)
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw InvalidValue(key, value, "a Boolean value");
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

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= minValue &&
            parsed <= maxValue)
        {
            return parsed;
        }

        throw InvalidValue(
            key,
            value,
            $"an integer between {minValue.ToString(CultureInfo.InvariantCulture)} and {maxValue.ToString(CultureInfo.InvariantCulture)}");
    }

    private static InvalidOperationException InvalidValue(string key, string value, string expected)
    {
        return new InvalidOperationException(
            $"Configuration value '{SectionName}:{key}' must be {expected}; received '{value}'.");
    }
}
