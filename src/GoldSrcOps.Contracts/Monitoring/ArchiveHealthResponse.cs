namespace GoldSrcOps.Contracts.Monitoring;

public sealed record ArchiveHealthResponse(DateTimeOffset EvaluatedAtUtc, string ObservationState, DateTimeOffset? ObservedAtUtc,
    bool? HasScheduledSuccess, DateTimeOffset? LastSuccessAtUtc, double? LastSuccessAgeSeconds,
    string WarningsState, DateTimeOffset? WarningsObservedAtUtc, IReadOnlyList<ArchiveWarningResponse> Warnings);
