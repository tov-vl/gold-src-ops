using System.Globalization;
using System.Text;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal static class RetentionHealthReport
{
    public static void Append(StringBuilder output, RetentionHealthResponse? source, DateTimeOffset generatedAt)
    {
        var view = RetentionHealthView.Create(source, generatedAt);
        var evaluated = source?.EvaluatedAtUtc;
        if (evaluated < DateTimeOffset.UnixEpoch || evaluated > generatedAt)
        {
            evaluated = null;
        }

        output.AppendLine();
        output.AppendLine("Snapshot retention health (independent source)");
        output.Append("Retention snapshot evaluated: ").AppendLine(Time(evaluated));
        output.Append("Retention snapshot age (seconds): ").AppendLine(evaluated is { } time ? Number((generatedAt - time).TotalSeconds) : "Unknown");
        output.Append("Retention observation: ").AppendLine(view.ObservationState);
        output.Append("Retention worker last observed: ").AppendLine(Time(view.ObservedAtUtc));
        output.Append("Retention last cycle started: ").AppendLine(Time(view.LastCycleStartedAtUtc));
        output.Append("Retention last cycle completed: ").AppendLine(Time(view.LastCycleFinishedAtUtc));
        output.Append("Retention completion age (seconds): ").AppendLine(Number(view.CompletionAgeSeconds));
        output.Append("Retention last successful cycle: ").AppendLine(Time(view.LastSuccessfulCycleAtUtc));
        output.Append("Retention last failed cycle: ").AppendLine(Time(view.LastFailureAtUtc));
        output.Append("Retention last cycle duration (seconds): ").AppendLine(Number(view.LastCycleDurationSeconds));
        output.Append("Retention consecutive failures: ").AppendLine(view.ConsecutiveFailures?.ToString(CultureInfo.InvariantCulture) ?? "Unknown");
        output.Append("Retention cycle in progress: ").AppendLine(view.InProgress is { } progress ? progress ? "Yes" : "No" : "Unknown");
        output.Append("Retention observation budget (seconds): ").AppendLine(Number(view.CycleBudgetSeconds));
        output.Append("Retention warning snapshot: ").Append(view.WarningsState).Append("; observed: ").AppendLine(Time(view.WarningsObservedAtUtc));
        foreach (var definition in RetentionHealthView.Definitions)
        {
            var state = view.Warnings.SingleOrDefault(item => string.Equals(item.Name, definition.Name, StringComparison.Ordinal))?.State ?? "Unknown";
            output.Append(definition.Name).Append(": ").AppendLine(state);
            output.Append("First check: ").AppendLine(definition.FirstCheck);
        }

        output.AppendLine("Unknown and Stale do not establish current cleanup progress or absence of warnings. Disabled observations do not establish an active worker.");
        output.AppendLine("Worker observations must be nonfuture and at most 120 seconds old; API and warning evaluations must be at most 60 seconds old.");
        output.AppendLine("Completed empty and full-batch passes are worker progress. Success does not establish backlog size, database size or completion of all eligible cleanup.");
        output.AppendLine("The observation budget is operational headroom, not a query timeout or latency SLO. Rule states do not establish notification delivery.");
        output.AppendLine("Preserve timestamps and bounded logs before reviewed recovery; do not automatically restart, replay or change thresholds.");
        output.Append("Guide: ").AppendLine(RetentionHealthView.Runbook);
    }

    private static string Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Unknown";
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "Unknown";
}
