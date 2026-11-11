namespace GoldSrcOps.Application.Monitoring;

public sealed record ArchiveHealthDto(DateTimeOffset EvaluatedAtUtc, string ObservationState, DateTimeOffset? ObservedAtUtc,
    bool? HasScheduledSuccess, DateTimeOffset? LastSuccessAtUtc, double? LastSuccessAgeSeconds,
    string WarningsState, DateTimeOffset? WarningsObservedAtUtc, IReadOnlyList<ArchiveWarningDto> Warnings);
