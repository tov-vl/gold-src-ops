namespace GoldSrcOps.AvailabilityExporter;

internal sealed class GrafanaMetricsApiException : Exception
{
    public GrafanaMetricsApiException(string message)
        : base(message)
    {
    }

    public GrafanaMetricsApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
