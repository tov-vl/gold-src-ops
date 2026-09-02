using System.Globalization;
using OpenTelemetry.Exporter;

namespace GoldSrcOps.Api.Hosting;

internal sealed class OtlpMetricsOptions
{
    private const string SectionName = "Telemetry:Otlp";

    public bool Enabled { get; init; }

    public Uri? Endpoint { get; init; }

    public OtlpExportProtocol Protocol { get; init; } = OtlpExportProtocol.Grpc;

    public int ExportIntervalMilliseconds { get; init; } = 60_000;

    public int ExportTimeoutMilliseconds { get; init; } = 30_000;

    public static OtlpMetricsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var options = new OtlpMetricsOptions
        {
            Enabled = ReadBool(section, "Enabled", defaultValue: false),
            Endpoint = ReadEndpoint(section),
            Protocol = ReadProtocol(section),
            ExportIntervalMilliseconds = ReadInt(
                section,
                "ExportIntervalMilliseconds",
                defaultValue: 60_000,
                minValue: 1_000,
                maxValue: 3_600_000),
            ExportTimeoutMilliseconds = ReadInt(
                section,
                "ExportTimeoutMilliseconds",
                defaultValue: 30_000,
                minValue: 100,
                maxValue: 300_000)
        };

        if (options.Enabled && options.Endpoint is null)
        {
            throw InvalidValue("Endpoint", "an absolute HTTP or HTTPS URL when OTLP metrics export is enabled");
        }

        if (options.ExportTimeoutMilliseconds > options.ExportIntervalMilliseconds)
        {
            throw new InvalidOperationException(
                "Configuration value 'Telemetry:Otlp:ExportTimeoutMilliseconds' must not exceed " +
                "'Telemetry:Otlp:ExportIntervalMilliseconds'.");
        }

        return options;
    }

    private static Uri? ReadEndpoint(IConfiguration section)
    {
        var rawEndpoint = section["Endpoint"];
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            return null;
        }

        if (!Uri.TryCreate(rawEndpoint.Trim(), UriKind.Absolute, out var endpoint) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            (!endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidValue("Endpoint", "an absolute HTTP or HTTPS URL without user information, query, or fragment");
        }

        return endpoint;
    }

    private static OtlpExportProtocol ReadProtocol(IConfiguration section)
    {
        var value = section["Protocol"];
        if (value is null)
        {
            return OtlpExportProtocol.Grpc;
        }

        var normalizedValue = value.Trim();
        if (normalizedValue.Equals("grpc", StringComparison.OrdinalIgnoreCase))
        {
            return OtlpExportProtocol.Grpc;
        }

        if (normalizedValue.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase))
        {
            return OtlpExportProtocol.HttpProtobuf;
        }

        throw InvalidValue("Protocol", "'grpc' or 'http/protobuf'");
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
                    $"an integer between {minValue.ToString(CultureInfo.InvariantCulture)} and " +
                    maxValue.ToString(CultureInfo.InvariantCulture));
    }

    private static InvalidOperationException InvalidValue(string key, string expected) =>
        new($"Configuration value '{SectionName}:{key}' must be {expected}.");
}
