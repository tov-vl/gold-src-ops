using System.Diagnostics.Metrics;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.Application.Telemetry;

public enum CommandDispatchMetricResult
{
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    AuthenticationFailed = 4
}

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

    private static readonly Counter<int> CommandsQueued = Meter.CreateCounter<int>(
        "goldsrcops.commands.queued",
        description: "Number of commands queued by command type.");

    private static readonly Counter<int> CommandsDispatched = Meter.CreateCounter<int>(
        "goldsrcops.commands.dispatched",
        description: "Number of commands dispatched to the RCON executor by command type.");

    private static readonly Counter<int> CommandsCompleted = Meter.CreateCounter<int>(
        "goldsrcops.commands.completed",
        description: "Number of completed command dispatches by command type and result.");

    private static readonly Counter<int> CommandsRecovered = Meter.CreateCounter<int>(
        "goldsrcops.commands.recovered",
        description: "Number of interrupted command executions recovered as failed.");

    private static readonly Counter<int> SnapshotRetentionRuns = Meter.CreateCounter<int>(
        "goldsrcops.snapshot_retention.runs",
        description: "Number of snapshot retention cleanup runs by result.");

    private static readonly Counter<int> SnapshotsDeleted = Meter.CreateCounter<int>(
        "goldsrcops.snapshot_retention.snapshots_deleted",
        description: "Number of expired poll snapshots deleted.");

    private static readonly Histogram<double> SnapshotRetentionDuration = Meter.CreateHistogram<double>(
        "goldsrcops.snapshot_retention.duration",
        unit: "s",
        description: "Duration of snapshot retention cleanup runs by result.");

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

    public static void RecordCommandQueued(ServerCommandType commandType)
    {
        CommandsQueued.Add(1, CommandTypeTag(commandType));
    }

    public static void RecordCommandDispatched(ServerCommandType commandType)
    {
        CommandsDispatched.Add(1, CommandTypeTag(commandType));
    }

    public static void RecordCommandCompleted(
        ServerCommandType commandType,
        CommandDispatchMetricResult result)
    {
        CommandsCompleted.Add(
            1,
            CommandTypeTag(commandType),
            CommandDispatchResultTag(result));
    }

    public static void RecordCommandsRecovered(int recoveredCount)
    {
        if (recoveredCount > 0)
        {
            CommandsRecovered.Add(recoveredCount);
        }
    }

    public static void RecordSnapshotRetentionCompleted(int deletedSnapshots, TimeSpan duration)
    {
        SnapshotRetentionRuns.Add(1, SuccessResultTag);
        AddIfPositive(SnapshotsDeleted, deletedSnapshots);
        SnapshotRetentionDuration.Record(duration.TotalSeconds, SuccessResultTag);
    }

    public static void RecordSnapshotRetentionFailed(TimeSpan duration)
    {
        SnapshotRetentionRuns.Add(1, FailureResultTag);
        SnapshotRetentionDuration.Record(duration.TotalSeconds, FailureResultTag);
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

    private static void AddIfPositive(Counter<int> counter, int value)
    {
        if (value > 0)
        {
            counter.Add(value);
        }
    }

    private static KeyValuePair<string, object?> CommandTypeTag(ServerCommandType commandType) =>
        new("command_type", commandType.ToString());

    private static KeyValuePair<string, object?> CommandDispatchResultTag(CommandDispatchMetricResult result)
    {
        return result switch
        {
            CommandDispatchMetricResult.Succeeded => new KeyValuePair<string, object?>("result", "succeeded"),
            CommandDispatchMetricResult.Failed => new KeyValuePair<string, object?>("result", "failed"),
            CommandDispatchMetricResult.TimedOut => new KeyValuePair<string, object?>("result", "timed_out"),
            CommandDispatchMetricResult.AuthenticationFailed => new KeyValuePair<string, object?>("result", "auth_failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Command dispatch metric result is not supported.")
        };
    }
}
