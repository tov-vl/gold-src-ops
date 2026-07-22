using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed class CommandDispatcherOptions
{
    private const string SectionName = "CommandDispatcher";

    public bool Enabled { get; init; } = true;

    public TimeSpan LoopDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public int MaxConcurrency { get; init; } = 4;

    public TimeSpan InterruptedAfter { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public static CommandDispatcherOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        return new CommandDispatcherOptions
        {
            Enabled = ReadBool(section, "Enabled", defaultValue: true),
            LoopDelay = TimeSpan.FromMilliseconds(
                ReadInt(section, "LoopDelayMilliseconds", defaultValue: 500, minValue: 10, maxValue: 60_000)),
            MaxConcurrency = ReadInt(section, "MaxConcurrency", defaultValue: 4, minValue: 1, maxValue: 32),
            InterruptedAfter = TimeSpan.FromSeconds(
                ReadInt(section, "InterruptedAfterSeconds", defaultValue: 30, minValue: 1, maxValue: 86_400)),
            RecoveryInterval = TimeSpan.FromSeconds(
                ReadInt(section, "RecoveryIntervalSeconds", defaultValue: 30, minValue: 1, maxValue: 3_600))
        };
    }

    private static bool ReadBool(IConfiguration section, string key, bool defaultValue)
    {
        var value = section[key];
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static int ReadInt(
        IConfiguration section,
        string key,
        int defaultValue,
        int minValue,
        int maxValue)
    {
        var value = section[key];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= minValue &&
            parsed <= maxValue
                ? parsed
                : defaultValue;
    }
}
