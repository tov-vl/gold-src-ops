namespace GoldSrcOps.AvailabilityExporter;

internal sealed record GrafanaMetricsApiOptions(
    Uri QueryEndpoint,
    string MetricsUser,
    string MetricsToken,
    string Job,
    string Probe,
    string Environment,
    string Role,
    string MonitorRevision,
    string Location,
    TimeSpan QueryStep,
    TimeSpan RequestTimeout,
    int MaximumResponseBytes);
