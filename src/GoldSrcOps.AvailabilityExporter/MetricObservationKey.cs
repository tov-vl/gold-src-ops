namespace GoldSrcOps.AvailabilityExporter;

internal sealed record MetricObservationKey(
    PrometheusSeriesKey Series,
    DateTimeOffset SourceTimestampUtc);
