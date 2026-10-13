using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

internal sealed class PublicApiMeasurementCapture : IDisposable
{
    private readonly MeterListener listener = new();
    public ConcurrentQueue<(double Seconds, Dictionary<string, object?> Tags)> Values { get; } = new();

    public PublicApiMeasurementCapture(PublicApiMetrics metrics)
    {
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (string.Equals(instrument.Name, PublicApiMetrics.DurationName, StringComparison.Ordinal) && ReferenceEquals(instrument.Meter.Scope, metrics))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            Values.Enqueue((value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal))));
        listener.Start();
    }

    public void Dispose() => listener.Dispose();
}
