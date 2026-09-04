namespace GoldSrcOps.Application.Availability;

public sealed record AvailabilityEvaluationRequest(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    DateTimeOffset EvaluatedAtUtc,
    string MonitorRevision,
    string Location,
    TimeSpan MissingGracePeriod,
    decimal TargetAvailability);
