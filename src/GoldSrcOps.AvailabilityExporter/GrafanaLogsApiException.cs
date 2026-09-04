namespace GoldSrcOps.AvailabilityExporter;

internal sealed class GrafanaLogsApiException : Exception
{
    public GrafanaLogsApiException(string message)
        : base(message)
    {
    }

    public GrafanaLogsApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
