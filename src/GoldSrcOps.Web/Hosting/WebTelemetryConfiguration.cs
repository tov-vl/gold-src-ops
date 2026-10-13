using GoldSrcOps.Web.Services;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace GoldSrcOps.Web.Hosting;

public static class WebTelemetryConfiguration
{
    public static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<PublicApiMetrics>();
        var enabledValue = configuration["Telemetry:Otlp:Enabled"];
        var enabled = false;
        if (enabledValue is not null && !bool.TryParse(enabledValue, out enabled))
        {
            throw new InvalidOperationException("Telemetry:Otlp:Enabled must be a boolean.");
        }

        if (!enabled)
        {
            return;
        }

        if (!Uri.TryCreate(configuration["Telemetry:Otlp:Endpoint"], UriKind.Absolute, out var endpoint) ||
            (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || !string.Equals(endpoint.AbsolutePath, "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Telemetry:Otlp:Endpoint must be an HTTP(S) origin without credentials.");
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("GoldSrcOps.Web"))
            .WithMetrics(metrics => metrics
                .AddMeter(PublicApiMetrics.MeterName)
                .AddView(PublicApiMetrics.DurationName, new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 3, 5, 5.5, 7.5, 10]
                })
                .AddOtlpExporter((exporter, reader) =>
                {
                    exporter.Endpoint = endpoint;
                    exporter.Protocol = OtlpExportProtocol.Grpc;
                    exporter.TimeoutMilliseconds = 30_000;
                    reader.PeriodicExportingMetricReaderOptions = new PeriodicExportingMetricReaderOptions
                    {
                        ExportIntervalMilliseconds = 60_000,
                        ExportTimeoutMilliseconds = 30_000
                    };
                }));
    }
}
