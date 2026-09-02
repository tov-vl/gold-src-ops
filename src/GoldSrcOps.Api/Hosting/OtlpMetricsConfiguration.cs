using OpenTelemetry.Metrics;

namespace GoldSrcOps.Api.Hosting;

internal static class OtlpMetricsConfiguration
{
    public static MeterProviderBuilder AddConfiguredOtlpExporter(
        this MeterProviderBuilder builder,
        OtlpMetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return builder;
        }

        var endpoint = options.Endpoint ?? throw new InvalidOperationException(
            "An OTLP metrics endpoint is required when export is enabled.");

        return builder.AddOtlpExporter((exporterOptions, metricReaderOptions) =>
        {
            exporterOptions.Endpoint = endpoint;
            exporterOptions.Protocol = options.Protocol;
            exporterOptions.TimeoutMilliseconds = options.ExportTimeoutMilliseconds;
            metricReaderOptions.PeriodicExportingMetricReaderOptions = new PeriodicExportingMetricReaderOptions
            {
                ExportIntervalMilliseconds = options.ExportIntervalMilliseconds,
                ExportTimeoutMilliseconds = options.ExportTimeoutMilliseconds
            };
        });
    }
}
