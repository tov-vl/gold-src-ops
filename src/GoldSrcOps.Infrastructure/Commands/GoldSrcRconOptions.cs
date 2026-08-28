using System.Globalization;
using GoldSrcOps.Domain.Commands;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed class GoldSrcRconOptions
{
    private const string SectionName = "Rcon";
    private const int DefaultTimeoutMilliseconds = 3_000;
    private const int DefaultResponseDrainMilliseconds = 100;
    private const int DefaultMaxResponseDatagrams = 32;
    private const int DefaultMaxResponseBytes = 64 * 1_024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(DefaultTimeoutMilliseconds);

    public int MaxResponseLength { get; init; } = CommandExecution.MaxResultLength;

    public TimeSpan ResponseDrainInterval { get; init; } =
        TimeSpan.FromMilliseconds(DefaultResponseDrainMilliseconds);

    public int MaxResponseDatagrams { get; init; } = DefaultMaxResponseDatagrams;

    public int MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;

    public static GoldSrcRconOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        return new GoldSrcRconOptions
        {
            Timeout = TimeSpan.FromMilliseconds(
                ReadPositiveInt(section, "TimeoutMilliseconds", DefaultTimeoutMilliseconds)),
            MaxResponseLength = Math.Min(
                CommandExecution.MaxResultLength,
                ReadPositiveInt(section, "MaxResponseLength", CommandExecution.MaxResultLength)),
            ResponseDrainInterval = TimeSpan.FromMilliseconds(
                ReadBoundedInt(
                    section,
                    "ResponseDrainMilliseconds",
                    DefaultResponseDrainMilliseconds,
                    minValue: 10,
                    maxValue: 1_000)),
            MaxResponseDatagrams = ReadBoundedInt(
                section,
                "MaxResponseDatagrams",
                DefaultMaxResponseDatagrams,
                minValue: 1,
                maxValue: 256),
            MaxResponseBytes = ReadBoundedInt(
                section,
                "MaxResponseBytes",
                DefaultMaxResponseBytes,
                minValue: 5,
                maxValue: 1_024 * 1_024)
        };
    }

    private static int ReadPositiveInt(IConfiguration section, string key, int defaultValue)
    {
        var value = section[key];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private static int ReadBoundedInt(
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
                : throw new InvalidOperationException(
                    $"Configuration value '{SectionName}:{key}' must be an integer between " +
                    $"{minValue.ToString(CultureInfo.InvariantCulture)} and " +
                    $"{maxValue.ToString(CultureInfo.InvariantCulture)}.");
    }
}
