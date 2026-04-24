using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed class GoldSrcPollingOptions
{
    private const string SectionName = "Polling";

    public bool Enabled { get; init; } = true;

    public TimeSpan LoopDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public int BatchSize { get; init; } = 50;

    public int IncidentFailureThreshold { get; init; } = 3;

    public static GoldSrcPollingOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        return new GoldSrcPollingOptions
        {
            Enabled = ReadBool(section, "Enabled", defaultValue: true),
            LoopDelay = TimeSpan.FromSeconds(ReadPositiveInt(section, "LoopDelaySeconds", defaultValue: 5)),
            QueryTimeout = TimeSpan.FromMilliseconds(ReadPositiveInt(section, "QueryTimeoutMilliseconds", defaultValue: 3000)),
            BatchSize = ReadPositiveInt(section, "BatchSize", defaultValue: 50),
            IncidentFailureThreshold = ReadPositiveInt(section, "IncidentFailureThreshold", defaultValue: 3)
        };
    }

    private static bool ReadBool(IConfiguration section, string key, bool defaultValue)
    {
        var value = section[key];
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static int ReadPositiveInt(IConfiguration section, string key, int defaultValue)
    {
        var value = section[key];
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }
}
