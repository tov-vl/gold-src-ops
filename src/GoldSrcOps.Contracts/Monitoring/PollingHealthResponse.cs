namespace GoldSrcOps.Contracts.Monitoring;

public sealed record PollingHealthResponse(DateTimeOffset EvaluatedAtUtc, string ObservationState, DateTimeOffset? ObservedAtUtc,
    DateTimeOffset? LastCycleStartedAtUtc, DateTimeOffset? LastCycleFinishedAtUtc, double? CompletionAgeSeconds, double? CycleBudgetSeconds,
    string WarningsState, DateTimeOffset? WarningsObservedAtUtc, IReadOnlyList<PollingWarningResponse> Warnings);
