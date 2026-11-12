namespace GoldSrcOps.Application.Monitoring;

public sealed record RetentionHealthDto(DateTimeOffset EvaluatedAtUtc, string ObservationState, DateTimeOffset? ObservedAtUtc,
    DateTimeOffset? LastCycleStartedAtUtc, DateTimeOffset? LastCycleFinishedAtUtc, double? CompletionAgeSeconds, double? CycleBudgetSeconds,
    string WarningsState, DateTimeOffset? WarningsObservedAtUtc, IReadOnlyList<RetentionWarningDto> Warnings,
    DateTimeOffset? LastSuccessfulCycleAtUtc = null, DateTimeOffset? LastFailureAtUtc = null, double? LastCycleDurationSeconds = null, int? ConsecutiveFailures = null, bool? InProgress = null);
