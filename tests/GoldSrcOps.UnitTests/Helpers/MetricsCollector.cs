using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace GoldSrcOps.UnitTests.Helpers;

internal sealed class MetricsCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<CollectedMetric> _measurements = new();

    public MetricsCollector(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>(RecordMeasurement);
        _listener.SetMeasurementEventCallback<double>(RecordMeasurement);
        _listener.Start();
    }

    public IReadOnlyCollection<CollectedMetric> Measurements => _measurements.ToArray();

    public void CollectObservableMetrics() => _listener.RecordObservableInstruments();

    public void Dispose()
    {
        _listener.Dispose();
    }

    private void RecordMeasurement(
        Instrument instrument,
        int measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        RecordMeasurement(instrument, (double)measurement, tags, state);
    }

    private void RecordMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        var tagDictionary = tags
            .ToArray()
            .ToDictionary(
                static tag => tag.Key,
                static tag => tag.Value,
                StringComparer.Ordinal);

        _measurements.Enqueue(new CollectedMetric(instrument.Name, measurement, tagDictionary));
    }
}

internal sealed record CollectedMetric(
    string Name,
    double Value,
    IReadOnlyDictionary<string, object?> Tags);
