using System.Diagnostics.Metrics;
using GoldSrcOps.Application.Servers;

namespace GoldSrcOps.Application.Telemetry;

public static class GoldSrcOpsMetrics
{
    public const string MeterName = "GoldSrcOps";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<int> PollingRuns = Meter.CreateCounter<int>(
        "goldsrcops.polling.runs",
        description: "Number of completed polling runs.");

    private static readonly Counter<int> ServerPollAttempts = Meter.CreateCounter<int>(
        "goldsrcops.polling.server_poll_attempts",
        description: "Number of server poll attempts by result.");

    private static readonly Counter<int> IncidentTransitions = Meter.CreateCounter<int>(
        "goldsrcops.polling.incident_transitions",
        description: "Number of availability incident transitions observed during polling.");

    private static readonly KeyValuePair<string, object?> SuccessResultTag = new("result", "success");

    private static readonly KeyValuePair<string, object?> FailureResultTag = new("result", "failure");

    private static readonly KeyValuePair<string, object?> OpenedTransitionTag = new("transition", "opened");

    private static readonly KeyValuePair<string, object?> ClosedTransitionTag = new("transition", "closed");

    public static void RecordPollingRun(ServerPollingResult result)
    {
        PollingRuns.Add(1);
        AddIfPositive(ServerPollAttempts, result.SuccessfulPolls, SuccessResultTag);
        AddIfPositive(ServerPollAttempts, result.FailedPolls, FailureResultTag);
        AddIfPositive(IncidentTransitions, result.OpenedIncidents, OpenedTransitionTag);
        AddIfPositive(IncidentTransitions, result.ClosedIncidents, ClosedTransitionTag);
    }

    private static void AddIfPositive(
        Counter<int> counter,
        int value,
        KeyValuePair<string, object?> tag)
    {
        if (value > 0)
        {
            counter.Add(value, tag);
        }
    }
}
