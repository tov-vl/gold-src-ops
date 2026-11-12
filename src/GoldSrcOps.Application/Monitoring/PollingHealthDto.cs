namespace GoldSrcOps.Application.Monitoring;

public sealed record PollingHealthDto(DateTimeOffset EvaluatedAtUtc, string ObservationState, DateTimeOffset? ObservedAtUtc,
    DateTimeOffset? LastCycleStartedAtUtc, DateTimeOffset? LastCycleFinishedAtUtc, double? CompletionAgeSeconds, double? CycleBudgetSeconds,
    string WarningsState, DateTimeOffset? WarningsObservedAtUtc, IReadOnlyList<PollingWarningDto> Warnings);
