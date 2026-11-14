using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal static class ArchiveHealthView
{
    public const string Path = "/operator/archive-health";
    public const string Runbook = "https://github.com/tov-vl/gold-src-ops/blob/main/docs/v2.117-archive-reliability.md";
    public static IReadOnlyList<Warning> Definitions { get; } = Array.AsReadOnly<Warning>([
        new Warning("ArchiveScheduleMonitorUnavailable", "Observer unavailable", "Check the independent timer, service and private Collector. Archive state is unknown."),
        new Warning("ArchiveScheduleObservationInvalid", "Observation invalid", "Check public GitHub API availability/rate limiting and the fixed workflow/job contract."),
        new Warning("ArchiveScheduleDelayed", "Automatic Archive delayed", "Inspect trigger history, the active writer and failed jobs. Do not automatically dispatch or retry."),
        new Warning("ArchiveScheduleCatchupAtRisk", "Catch-up window at risk", "Review the approaching 24-hour boundary and source retention before a separately reviewed recovery decision."),
    ]);

    public static ArchiveHealthResponse Create(ArchiveHealthResponse? source, DateTimeOffset now)
    {
        var result = new ArchiveHealthResponse(now, "Unknown", ObservedAtUtc: null, HasScheduledSuccess: null, LastSuccessAtUtc: null, LastSuccessAgeSeconds: null, "Unknown", WarningsObservedAtUtc: null, []);
        if (source is null || source.EvaluatedAtUtc < now.AddSeconds(-60) || source.EvaluatedAtUtc > now)
        {
            return result;
        }

        result = result with { EvaluatedAtUtc = source.EvaluatedAtUtc };
        if (source.ObservedAtUtc is { } observed && observed >= DateTimeOffset.UnixEpoch && observed <= source.EvaluatedAtUtc)
        {
            result = result with { ObservedAtUtc = observed };
            if (observed < now.AddSeconds(-1800) && source.ObservationState is "Available" or "Stale")
            {
                result = result with { ObservationState = "Stale" };
            }
            else if (source is { ObservationState: "Available", TriggerSource: "Unknown" or "GitHubSchedule" or "ExternalTimer" })
            {
                result = source switch
                {
                    { HasScheduledSuccess: false, LastSuccessAtUtc: null, LastSuccessAgeSeconds: null } => result with { ObservationState = "Available", HasScheduledSuccess = false, TriggerSource = source.TriggerSource },
                    { HasScheduledSuccess: true, LastSuccessAtUtc: { } success, LastSuccessAgeSeconds: { } age } when success >= DateTimeOffset.UnixEpoch && success <= observed && double.IsFinite(age) && age >= 0 && Math.Abs(age - (source.EvaluatedAtUtc - success).TotalSeconds) < 1 => result with
                    {
                        ObservationState = "Available",
                        HasScheduledSuccess = true,
                        LastSuccessAtUtc = success,
                        LastSuccessAgeSeconds = (now - success).TotalSeconds,
                        TriggerSource = source.TriggerSource,
                    },
                    _ => result,
                };
            }
        }

        if (source.WarningsObservedAtUtc is not { } evaluated || evaluated < DateTimeOffset.UnixEpoch || evaluated > source.EvaluatedAtUtc)
        {
            return result;
        }

        if (evaluated < now.AddSeconds(-60) && source.WarningsState is "Available" or "Stale")
        {
            return result with { WarningsState = "Stale", WarningsObservedAtUtc = evaluated };
        }

        if (source is { WarningsState: "Available", Warnings: { Count: 4 } warnings } && warnings.Cast<ArchiveWarningResponse?>().All(static item => item is not null) &&
            warnings.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Take(5).Count() == 4 &&
            warnings.All(static item => Definitions.Any(definition => string.Equals(definition.Name, item.Name, StringComparison.Ordinal)) && item.State is "Inactive" or "Pending" or "Firing"))
        {
            result = result with
            {
                WarningsState = "Available",
                WarningsObservedAtUtc = evaluated,
                Warnings = [.. warnings.Select(static item => new ArchiveWarningResponse(item.Name, item.State)),],
            };
        }

        return result;
    }

    public static string TriggerLabel(string source) => source switch
    {
        "GitHubSchedule" => "GitHub schedule",
        "ExternalTimer" => "External timer",
        _ => "Unknown",
    };

    internal sealed record Warning(string Name, string Title, string FirstCheck);
}
