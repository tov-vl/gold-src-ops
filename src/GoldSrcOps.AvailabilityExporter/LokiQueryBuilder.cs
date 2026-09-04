namespace GoldSrcOps.AvailabilityExporter;

internal static class LokiQueryBuilder
{
    public static string BuildFailedHttpProbeQuery(GrafanaLogsApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return "{" +
            "check_name=\"http\"," +
            "source=\"synthetic-monitoring-agent\"," +
            $"job=\"{EscapeLabelValue(options.Job)}\"," +
            $"probe=\"{EscapeLabelValue(options.Probe)}\"," +
            $"label_environment=\"{EscapeLabelValue(options.Environment)}\"," +
            $"label_role=\"{EscapeLabelValue(options.Role)}\"," +
            $"label_monitor_revision=\"{EscapeLabelValue(options.MonitorRevision)}\"," +
            "probe_success=\"0\"}";
    }

    private static string EscapeLabelValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
