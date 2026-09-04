using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.AvailabilityExporter;

internal sealed record CanonicalAvailabilityJsonRecord(
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string MonitorRevision,
    string Location,
    AvailabilityProbeRole Role,
    string ExecutionId,
    AvailabilityOutcome Outcome,
    int? HttpStatus,
    long? DurationMs)
{
    public static CanonicalAvailabilityJsonRecord FromDomain(CanonicalAvailabilityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CanonicalAvailabilityJsonRecord(
            result.ScheduledAtUtc,
            result.StartedAtUtc,
            result.CompletedAtUtc,
            result.MonitorRevision,
            result.Location,
            result.Role,
            result.ExecutionId,
            result.Outcome,
            result.HttpStatus,
            result.DurationMilliseconds);
    }

    public CanonicalAvailabilityResult ToDomain() =>
        new(
            ScheduledAtUtc,
            StartedAtUtc,
            CompletedAtUtc,
            MonitorRevision,
            Location,
            Role,
            ExecutionId,
            Outcome,
            HttpStatus,
            DurationMs);
}
