namespace GoldSrcOps.AvailabilityExporter;

internal sealed record PrometheusSeriesKey(
    string Job,
    string Instance,
    string Probe,
    string ConfigVersion);
