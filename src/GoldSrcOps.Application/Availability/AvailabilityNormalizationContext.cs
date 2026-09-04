namespace GoldSrcOps.Application.Availability;

public sealed record AvailabilityNormalizationContext(
    string ProviderName,
    string MonitorRevision,
    string Location,
    AvailabilityProbeRole Role);
