using System.Globalization;
using System.Text.Json;

namespace GoldSrcOps.AvailabilityExporter;

internal static class PrometheusMatrixParser
{
    public static IReadOnlyList<PrometheusSeries> Parse(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            return ParseCore(utf8Json);
        }
        catch (Exception exception) when (exception is
            JsonException or
            FormatException or
            OverflowException or
            ArgumentOutOfRangeException or
            InvalidOperationException)
        {
            throw new InvalidDataException("The Prometheus matrix payload is malformed.", exception);
        }
    }

    private static List<PrometheusSeries> ParseCore(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;

        if (!TryGetString(root, "status", out var status) ||
            !string.Equals(status, "success", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The metrics API returned an unsuccessful response.");
        }

        if (!root.TryGetProperty("data", out var data) ||
            !TryGetString(data, "resultType", out var resultType) ||
            !string.Equals(resultType, "matrix", StringComparison.Ordinal) ||
            !data.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The metrics API response is not a Prometheus matrix.");
        }

        var series = new List<PrometheusSeries>();
        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("metric", out var metric) ||
                metric.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("values", out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("A Prometheus matrix series is malformed.");
            }

            var key = new PrometheusSeriesKey(
                GetRequiredLabel(metric, "job"),
                GetRequiredLabel(metric, "instance"),
                GetRequiredLabel(metric, "probe"),
                GetRequiredLabel(metric, "config_version"));
            var samples = new List<PrometheusSample>();

            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
                {
                    throw new InvalidDataException("A Prometheus matrix sample is malformed.");
                }

                var timestamp = value[0].GetDouble();
                var text = value[1].GetString();
                if (!double.IsFinite(timestamp) ||
                    !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sampleValue) ||
                    !double.IsFinite(sampleValue))
                {
                    throw new InvalidDataException("A Prometheus matrix sample is not finite.");
                }

                samples.Add(new PrometheusSample(FromUnixSeconds(timestamp), sampleValue));
            }

            series.Add(new PrometheusSeries(key, samples));
        }

        return series;
    }

    internal static DateTimeOffset FromUnixSeconds(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        var milliseconds = checked((long)Math.Round(
            seconds * 1000d,
            MidpointRounding.AwayFromZero));
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static string GetRequiredLabel(JsonElement metric, string name)
    {
        if (!TryGetString(metric, name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("A required Prometheus series label is missing.");
        }

        return value;
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }
}
