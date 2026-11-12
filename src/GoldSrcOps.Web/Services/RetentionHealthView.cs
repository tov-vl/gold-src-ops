using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal static class RetentionHealthView
{
    public const string Path = "/operator/retention-health";
    public const string Runbook = "https://github.com/tov-vl/gold-src-ops/blob/main/docs/v2.127-retention-liveness.md";
    public static IReadOnlyList<Warning> Definitions { get; } = Array.AsReadOnly<Warning>([
        new Warning("SnapshotRetentionMonitorUnavailable", "Retention observation unavailable", "Check observation freshness, enabled configuration and duplicate active sources. The production contract expects one active cleanup worker."),
        new Warning("SnapshotRetentionCycleStalled", "Cycle progress stale", "Preserve cycle timestamps and check cleanup batch limits and database availability. Do not replay a pass automatically."),
        new Warning("SnapshotRetentionCycleFailed", "Worker cycles failing", "Inspect bounded worker logs and persistence dependencies. A successful empty pass is progress; do not trigger cleanup from this page.")
    ]);

    public static RetentionHealthResponse Create(RetentionHealthResponse? source, DateTimeOffset now)
    {
        var result = new RetentionHealthResponse(now, "Unknown", ObservedAtUtc: null, LastCycleStartedAtUtc: null, LastCycleFinishedAtUtc: null,
            CompletionAgeSeconds: null, CycleBudgetSeconds: null, "Unknown", WarningsObservedAtUtc: null, []);
        if (source is null || source.EvaluatedAtUtc < now.AddSeconds(-60) || source.EvaluatedAtUtc > now)
        {
            return result;
        }

        result = result with { EvaluatedAtUtc = source.EvaluatedAtUtc };
        if (source.ObservationState is "Unknown" or "Running" or "Error" or "Disabled" or "Stale" && source.ObservedAtUtc is { } observed &&
            observed >= DateTimeOffset.UnixEpoch && observed <= source.EvaluatedAtUtc)
        {
            result = result with { ObservedAtUtc = observed };
            if (observed < now.AddSeconds(-120))
            {
                result = result with { ObservationState = "Stale" };
            }
            else if (source.CycleBudgetSeconds is { } budget && double.IsFinite(budget) && budget is >= 300 and <= 172860 &&
                (source.LastCycleStartedAtUtc is null || source.LastCycleStartedAtUtc >= DateTimeOffset.UnixEpoch && source.LastCycleStartedAtUtc <= source.EvaluatedAtUtc) &&
                (source.LastCycleFinishedAtUtc is null && source.CompletionAgeSeconds is null ||
                    source.LastCycleFinishedAtUtc is { } finished && finished >= DateTimeOffset.UnixEpoch && finished <= source.EvaluatedAtUtc && source.LastCycleStartedAtUtc is not null &&
                    source.CompletionAgeSeconds is { } age && double.IsFinite(age) && age >= 0 && Math.Abs(age - (source.EvaluatedAtUtc - finished).TotalSeconds) < 1) &&
                (source.ObservationState is not ("Running" or "Error") || source.LastCycleFinishedAtUtc is not null) &&
                (source.LastSuccessfulCycleAtUtc is null || source.LastSuccessfulCycleAtUtc >= DateTimeOffset.UnixEpoch && source.LastSuccessfulCycleAtUtc <= source.EvaluatedAtUtc) &&
                (source.LastFailureAtUtc is null || source.LastFailureAtUtc >= DateTimeOffset.UnixEpoch && source.LastFailureAtUtc <= source.EvaluatedAtUtc) &&
                source.LastCycleDurationSeconds is { } duration && double.IsFinite(duration) && duration >= 0 &&
                source.ConsecutiveFailures is >= 0 and <= 1000000 && source.InProgress is not null)
            {
                var currentAge = source.LastCycleFinishedAtUtc is { } completion ? (now - completion).TotalSeconds : (double?)null;
                result = result with
                {
                    ObservationState = source.ObservationState is not "Disabled" && currentAge > budget ? "Stale" : source.ObservationState,
                    LastCycleStartedAtUtc = source.LastCycleStartedAtUtc,
                    LastCycleFinishedAtUtc = source.LastCycleFinishedAtUtc,
                    CompletionAgeSeconds = currentAge,
                    CycleBudgetSeconds = budget,
                    LastSuccessfulCycleAtUtc = source.LastSuccessfulCycleAtUtc,
                    LastFailureAtUtc = source.LastFailureAtUtc,
                    LastCycleDurationSeconds = source.LastCycleDurationSeconds,
                    ConsecutiveFailures = source.ConsecutiveFailures,
                    InProgress = source.InProgress
                };
            }
            else if (source.ObservationState is "Disabled" && source.LastCycleStartedAtUtc is null && source.LastCycleFinishedAtUtc is null && source.CompletionAgeSeconds is null)
            {
                result = result with { ObservationState = "Disabled" };
            }
        }

        if (source.WarningsObservedAtUtc is { } evaluated && evaluated >= DateTimeOffset.UnixEpoch && evaluated <= source.EvaluatedAtUtc)
        {
            if (evaluated < now.AddSeconds(-60) && source.WarningsState is "Available" or "Stale")
            {
                result = result with { WarningsState = "Stale", WarningsObservedAtUtc = evaluated };
            }
            else if (source.WarningsState is "Available" && source.Warnings is { Count: 3 } warnings && warnings.All(static item => item is not null) &&
                warnings.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() == 3 &&
                warnings.All(static item => Definitions.Any(definition => string.Equals(definition.Name, item.Name, StringComparison.Ordinal)) && item.State is "Inactive" or "Pending" or "Firing"))
            {
                result = result with
                {
                    WarningsState = "Available",
                    WarningsObservedAtUtc = evaluated,
                    Warnings = [.. warnings.Select(static item => new RetentionWarningResponse(item.Name, item.State))]
                };
            }
        }

        return result;
    }

    internal sealed record Warning(string Name, string Title, string FirstCheck);
}
