namespace GoldSrcOps.AvailabilityExporter;

internal sealed record PrometheusSeries(
    PrometheusSeriesKey Key,
    IReadOnlyList<PrometheusSample> Samples);
