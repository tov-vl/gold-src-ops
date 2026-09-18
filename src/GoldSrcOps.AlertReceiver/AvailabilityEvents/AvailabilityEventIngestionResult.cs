namespace GoldSrcOps.AlertReceiver.AvailabilityEvents;

internal sealed record AvailabilityEventIngestionResult(
    AvailabilityEventIngestionDisposition Disposition,
    string? Error = null)
{
    public static AvailabilityEventIngestionResult Accepted() =>
        new(AvailabilityEventIngestionDisposition.Accepted);

    public static AvailabilityEventIngestionResult Duplicate() =>
        new(AvailabilityEventIngestionDisposition.Duplicate);

    public static AvailabilityEventIngestionResult Conflict(string error) =>
        new(AvailabilityEventIngestionDisposition.Conflict, error);
}

internal enum AvailabilityEventIngestionDisposition
{
    Accepted,
    Duplicate,
    Conflict,
}
