using System.Diagnostics.Metrics;

namespace GoldSrcOps.Web.Services;

public sealed class PublicApiMetrics : IDisposable
{
    public const string MeterName = "GoldSrcOps.Web.PublicApi";
    public const string DurationName = "goldsrcops.web.public_api.duration";
    private readonly Meter meter;
    private readonly Histogram<double> duration;

    public PublicApiMetrics()
    {
        meter = new Meter(new MeterOptions(MeterName) { Scope = this });
        duration = meter.CreateHistogram<double>(DurationName, unit: "s",
            description: "Complete public API reads, including JSON, by bounded operation and outcome.");
    }

    internal void Record(string operation, string outcome, TimeSpan elapsed) =>
        duration.Record(elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void Dispose() => meter.Dispose();
}
