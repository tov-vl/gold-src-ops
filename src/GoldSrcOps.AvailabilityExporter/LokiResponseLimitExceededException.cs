namespace GoldSrcOps.AvailabilityExporter;

internal sealed class LokiResponseLimitExceededException : Exception
{
    public LokiResponseLimitExceededException()
        : base("The logs API response exceeded the line limit.")
    {
    }
}
