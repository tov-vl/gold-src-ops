namespace GoldSrcOps.AvailabilityExporter;

internal static class PromQlQueryBuilder
{
    private static readonly HashSet<string> AllowedMetrics = new(StringComparer.Ordinal)
    {
        "probe_success",
        "probe_http_status_code",
        "probe_duration_seconds",
    };

    public static string Build(
        string metric,
        GrafanaMetricsApiOptions options,
        bool returnSourceTimestamp)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!AllowedMetrics.Contains(metric))
        {
            throw new ArgumentOutOfRangeException(nameof(metric));
        }

        var job = EscapeLabelValue(options.Job);
        var probe = EscapeLabelValue(options.Probe);
        var environment = EscapeLabelValue(options.Environment);
        var role = EscapeLabelValue(options.Role);
        var revision = EscapeLabelValue(options.MonitorRevision);
        var metricSelector = $"{metric}{{job=\"{job}\",probe=\"{probe}\"}}";
        var left = returnSourceTimestamp ? $"timestamp({metricSelector})" : metricSelector;

        return $"{left} * on (job, instance, probe, config_version) " +
            $"sm_check_info{{job=\"{job}\",probe=\"{probe}\",label_environment=\"{environment}\"," +
            $"label_role=\"{role}\",label_monitor_revision=\"{revision}\"}}";
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
