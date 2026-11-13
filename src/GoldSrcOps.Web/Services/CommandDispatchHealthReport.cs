using System.Globalization;
using System.Text;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal static class CommandDispatchHealthReport
{
    public static void Append(StringBuilder output, CommandDispatchHealthResponse? source, DateTimeOffset generatedAt)
    {
        var view = CommandDispatchHealthView.Create(source, generatedAt);
        var evaluated = source?.EvaluatedAtUtc;
        if (evaluated < DateTimeOffset.UnixEpoch || evaluated > generatedAt)
        {
            evaluated = null;
        }

        output.AppendLine();
        output.AppendLine("Command dispatcher health (independent source)");
        output.Append("Command dispatcher snapshot evaluated: ").AppendLine(Time(evaluated));
        output.Append("Command dispatcher snapshot age (seconds): ").AppendLine(evaluated is { } time ? Number((generatedAt - time).TotalSeconds) : "Unknown");
        output.Append("Command dispatcher observation: ").AppendLine(view.ObservationState);
        output.Append("Command dispatcher worker last observed: ").AppendLine(Time(view.ObservedAtUtc));
        output.Append("Command dispatcher last cycle started: ").AppendLine(Time(view.LastCycleStartedAtUtc));
        output.Append("Command dispatcher last cycle completed: ").AppendLine(Time(view.LastCycleFinishedAtUtc));
        output.Append("Command dispatcher completion age (seconds): ").AppendLine(Number(view.CompletionAgeSeconds));
        output.Append("Command dispatcher last successful cycle: ").AppendLine(Time(view.LastSuccessfulCycleAtUtc));
        output.Append("Command dispatcher last failed cycle: ").AppendLine(Time(view.LastFailureAtUtc));
        output.Append("Command dispatcher last cycle duration (seconds): ").AppendLine(Number(view.LastCycleDurationSeconds));
        output.Append("Command dispatcher consecutive failures: ").AppendLine(view.ConsecutiveFailures?.ToString(CultureInfo.InvariantCulture) ?? "Unknown");
        output.Append("Command dispatcher cycle in progress: ").AppendLine(view.InProgress is { } progress ? progress ? "Yes" : "No" : "Unknown");
        output.Append("Command dispatcher observation budget (seconds): ").AppendLine(Number(view.CycleBudgetSeconds));
        output.Append("Command dispatcher warning snapshot: ").Append(view.WarningsState).Append("; observed: ").AppendLine(Time(view.WarningsObservedAtUtc));
        foreach (var definition in CommandDispatchHealthView.Definitions)
        {
            var state = view.Warnings.SingleOrDefault(item => string.Equals(item.Name, definition.Name, StringComparison.Ordinal))?.State ?? "Unknown";
            output.Append(definition.Name).Append(": ").AppendLine(state);
            output.Append("First check: ").AppendLine(definition.FirstCheck);
        }

        output.AppendLine("Unknown and Stale do not establish current dispatcher progress or absence of warnings. Disabled observations do not establish an active worker.");
        output.AppendLine("Worker observations must be nonfuture and at most 120 seconds old; API and warning evaluations must be at most 60 seconds old.");
        output.AppendLine("Completed cycles, including an empty queue, are worker progress. An individual failed RCON response is not a cycle failure. Running does not establish command delivery or an empty queue.");
        output.AppendLine("The observation budget is operational headroom, not a command timeout or latency SLO. Rule states do not establish notification delivery.");
        output.AppendLine("Preserve timestamps and bounded logs before reviewed recovery; do not automatically restart, replay or change thresholds.");
        output.Append("Guide: ").AppendLine(CommandDispatchHealthView.Runbook);
    }

    private static string Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Unknown";
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "Unknown";
}
