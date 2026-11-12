namespace GoldSrcOps.Contracts.Monitoring;

public sealed record RetentionHealthResponse(DateTimeOffset EvaluatedAtUtc, string ObservationState, DateTimeOffset? ObservedAtUtc,
    DateTimeOffset? LastCycleStartedAtUtc, DateTimeOffset? LastCycleFinishedAtUtc, double? CompletionAgeSeconds, double? CycleBudgetSeconds,
    string WarningsState, DateTimeOffset? WarningsObservedAtUtc, IReadOnlyList<RetentionWarningResponse> Warnings,
    DateTimeOffset? LastSuccessfulCycleAtUtc = null, DateTimeOffset? LastFailureAtUtc = null, double? LastCycleDurationSeconds = null, int? ConsecutiveFailures = null, bool? InProgress = null);
