namespace GoldSrcOps.Application.Availability;

public sealed record AvailabilityEvaluationReport(
    string EvaluatorRevision,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    DateTimeOffset EvaluatedAtUtc,
    string MonitorRevision,
    string Location,
    int ExpectedSlotCount,
    int EvaluatedSlotCount,
    int PendingSlotCount,
    int GoodSlotCount,
    int BadSlotCount,
    int MissingSlotCount,
    int DuplicateRecordCount,
    int IgnoredNonCanonicalAttemptCount,
    decimal? Availability,
    decimal TargetAvailability,
    int AllowedBadSlotCount,
    bool? MeetsTarget,
    AvailabilityOutcomeCounts Outcomes);
