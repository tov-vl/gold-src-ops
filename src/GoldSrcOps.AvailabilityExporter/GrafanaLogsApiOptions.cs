namespace GoldSrcOps.AvailabilityExporter;

internal sealed record GrafanaLogsApiOptions(
    Uri QueryEndpoint,
    string LogsUser,
    string LogsToken,
    string Job,
    string Probe,
    string Environment,
    string Role,
    string MonitorRevision,
    TimeSpan RequestTimeout,
    int MaximumResponseBytes,
    int MaximumLines,
    TimeSpan CorrelationTolerance)
{
    private const int DefaultMaximumResponseBytes = 8 * 1024 * 1024;
    private const int DefaultMaximumLines = 5_000;

    public static GrafanaLogsApiOptions? CreateOptional(
        string? endpointText,
        string? logsUser,
        string? logsToken,
        GrafanaMetricsApiOptions metricsOptions)
    {
        ArgumentNullException.ThrowIfNull(metricsOptions);

        var values = new[] { endpointText, logsUser, logsToken };
        if (values.All(static value => value is null))
        {
            return null;
        }

        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "Grafana logs enrichment requires URL, user, and token together.");
        }

        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("The logs API URL is invalid.");
        }

        return new GrafanaLogsApiOptions(
            endpoint,
            logsUser!,
            logsToken!,
            metricsOptions.Job,
            metricsOptions.Probe,
            metricsOptions.Environment,
            metricsOptions.Role,
            metricsOptions.MonitorRevision,
            TimeSpan.FromSeconds(30),
            DefaultMaximumResponseBytes,
            DefaultMaximumLines,
            TimeSpan.FromSeconds(10));
    }
}
