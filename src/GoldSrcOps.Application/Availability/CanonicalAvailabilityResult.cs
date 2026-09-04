namespace GoldSrcOps.Application.Availability;

public sealed record CanonicalAvailabilityResult(
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string MonitorRevision,
    string Location,
    AvailabilityProbeRole Role,
    string ExecutionId,
    AvailabilityOutcome Outcome,
    int? HttpStatus,
    long? DurationMilliseconds);
