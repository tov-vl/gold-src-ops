using System.Globalization;
using GoldSrcOps.Domain.Commands;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed class GoldSrcRconOptions
{
    private const string SectionName = "Rcon";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    public int MaxResponseLength { get; init; } = CommandExecution.MaxResultLength;

    public static GoldSrcRconOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        return new GoldSrcRconOptions
        {
            Timeout = TimeSpan.FromMilliseconds(ReadPositiveInt(section, "TimeoutMilliseconds", defaultValue: 3000)),
            MaxResponseLength = Math.Min(
                CommandExecution.MaxResultLength,
                ReadPositiveInt(section, "MaxResponseLength", CommandExecution.MaxResultLength))
        };
    }

    private static int ReadPositiveInt(IConfiguration section, string key, int defaultValue)
    {
        var value = section[key];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }
}
