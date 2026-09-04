using System.Globalization;
using System.Text.Json;

namespace GoldSrcOps.AvailabilityExporter;

internal static class LokiFailureDetailsParser
{
    private const long NanosecondsPerSecond = 1_000_000_000;
    private const long NanosecondsPerTick = 100;

    public static IReadOnlyList<ProbeFailureDetail> Parse(byte[] payload, int maximumLines)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);

        try
        {
            return ParseCore(payload, maximumLines);
        }
        catch (LokiResponseLimitExceededException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or
                                          InvalidOperationException or
                                          FormatException or
                                          OverflowException or
                                          ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The logs API returned an invalid response.");
        }
    }

    private static List<ProbeFailureDetail> ParseCore(byte[] payload, int maximumLines)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            !string.Equals(status.GetString(), "success", StringComparison.Ordinal) ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("resultType", out var resultType) ||
            resultType.ValueKind != JsonValueKind.String ||
            !string.Equals(resultType.GetString(), "streams", StringComparison.Ordinal) ||
            !data.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The logs API returned an invalid response.");
        }

        var details = new List<ProbeFailureDetail>();
        var lineCount = 0;

        foreach (var stream in result.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object ||
                !stream.TryGetProperty("values", out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The logs API returned an invalid response.");
            }

            foreach (var item in values.EnumerateArray())
            {
                lineCount = checked(lineCount + 1);
                if (lineCount > maximumLines)
                {
                    throw new LokiResponseLimitExceededException();
                }

                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2)
                {
                    throw new InvalidDataException("The logs API returned an invalid response.");
                }

                var elements = item.EnumerateArray();
                elements.MoveNext();
                var timestamp = ParseTimestamp(elements.Current);
                elements.MoveNext();
                if (elements.Current.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("The logs API returned an invalid response.");
                }

                var failureKind = ProbeFailureLogClassifier.Classify(elements.Current.GetString()!);
                if (failureKind is not null)
                {
                    details.Add(new ProbeFailureDetail(timestamp, failureKind.Value));
                }
            }
        }

        return details;
    }

    private static DateTimeOffset ParseTimestamp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String ||
            !long.TryParse(
                element.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var nanoseconds) ||
            nanoseconds < 0)
        {
            throw new InvalidDataException("The logs API returned an invalid response.");
        }

        var seconds = Math.DivRem(nanoseconds, NanosecondsPerSecond, out var remainder);
        return DateTimeOffset.FromUnixTimeSeconds(seconds)
            .AddTicks(remainder / NanosecondsPerTick);
    }
}
