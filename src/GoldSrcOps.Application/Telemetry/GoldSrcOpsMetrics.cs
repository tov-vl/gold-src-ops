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

public enum AlertDeliveryMetricResult
{
    Delivered = 1,
    RetryScheduled = 2,
    DeadLettered = 3,
    ClaimLost = 4,
    Interrupted = 5,
    Faulted = 6
}

public enum AlertReplayMetricResult
{
    Accepted = 1,
    Idempotent = 2,
    Conflict = 3,
    Invalid = 4
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

    private static readonly Counter<int> AlertEventsEnqueued = Meter.CreateCounter<int>(
        "goldsrcops.alerts.enqueued",
        description: "Number of incident alert events committed to the outbox.");

    private static readonly Counter<int> AlertDeliveryAttempts = Meter.CreateCounter<int>(
        "goldsrcops.alerts.delivery_attempts",
        description: "Number of completed alert delivery attempts by result.");

    private static readonly Counter<int> AlertsDelivered = Meter.CreateCounter<int>(
        "goldsrcops.alerts.delivered",
        description: "Number of alert messages successfully delivered and marked processed.");

    private static readonly Counter<int> AlertRetriesScheduled = Meter.CreateCounter<int>(
        "goldsrcops.alerts.retries_scheduled",
        description: "Number of alert delivery retries scheduled.");

    private static readonly Counter<int> AlertDeadLetters = Meter.CreateCounter<int>(
        "goldsrcops.alerts.dead_letters",
        description: "Number of alert messages moved to dead letter.");

    private static readonly Counter<int> AlertClaimsRecovered = Meter.CreateCounter<int>(
        "goldsrcops.alerts.claims_recovered",
        description: "Number of expired alert delivery claims recovered.");

    private static readonly Counter<int> AlertProcessedMessagesDeleted = Meter.CreateCounter<int>(
        "goldsrcops.alerts.processed_deleted",
        description: "Number of processed alert messages deleted by retention.");

    private static readonly Histogram<double> AlertDeliveryDuration = Meter.CreateHistogram<double>(
        "goldsrcops.alerts.delivery_duration",
        unit: "s",
        description: "Duration of alert delivery attempts by result.");

    private static readonly Counter<int> AlertReplayRequests = Meter.CreateCounter<int>(
        "goldsrcops.alerts.replay_requests",
        description: "Number of completed dead-letter replay requests by result.");

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

    private static double _alertPendingCount;

    private static double _alertOldestPendingAgeSeconds;

    private static double _alertDeadLetterCount;

    private static readonly KeyValuePair<string, object?> SuccessResultTag = new("result", "success");

    private static readonly KeyValuePair<string, object?> FailureResultTag = new("result", "failure");

    private static readonly KeyValuePair<string, object?> OpenedTransitionTag = new("transition", "opened");

    private static readonly KeyValuePair<string, object?> ClosedTransitionTag = new("transition", "closed");

    static GoldSrcOpsMetrics()
    {
        Meter.CreateObservableGauge(
            "goldsrcops.alerts.pending",
            () => Volatile.Read(ref _alertPendingCount),
            description: "Current number of pending alert outbox messages.");
        Meter.CreateObservableGauge(
            "goldsrcops.alerts.oldest_pending_age",
            () => Volatile.Read(ref _alertOldestPendingAgeSeconds),
            unit: "s",
            description: "Age of the oldest pending alert outbox message.");
        Meter.CreateObservableGauge(
            "goldsrcops.alerts.dead_letter_count",
            () => Volatile.Read(ref _alertDeadLetterCount),
            description: "Current number of dead-letter alert outbox messages.");
    }

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

    public static void RecordAlertEnqueued(string eventType)
    {
        AlertEventsEnqueued.Add(1, new KeyValuePair<string, object?>("event_type", eventType));
    }

    public static void RecordAlertDeliveryAttempt(
        AlertDeliveryMetricResult result,
        TimeSpan duration)
    {
        var resultTag = AlertDeliveryResultTag(result);
        AlertDeliveryAttempts.Add(1, resultTag);
        AlertDeliveryDuration.Record(duration.TotalSeconds, resultTag);

        switch (result)
        {
            case AlertDeliveryMetricResult.Delivered:
                AlertsDelivered.Add(1);
                break;
            case AlertDeliveryMetricResult.RetryScheduled:
                AlertRetriesScheduled.Add(1);
                break;
            case AlertDeliveryMetricResult.DeadLettered:
                RecordAlertDeadLetters(1);
                break;
            case AlertDeliveryMetricResult.ClaimLost:
            case AlertDeliveryMetricResult.Interrupted:
            case AlertDeliveryMetricResult.Faulted:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result,
                    "Alert delivery metric result is not supported.");
        }
    }

    public static void RecordAlertClaimsRecovered(int recoveredCount)
    {
        AddIfPositive(AlertClaimsRecovered, recoveredCount);
    }

    public static void RecordAlertDeadLetters(int deadLetterCount)
    {
        AddIfPositive(AlertDeadLetters, deadLetterCount);
    }

    public static void RecordAlertProcessedMessagesDeleted(int deletedCount)
    {
        AddIfPositive(AlertProcessedMessagesDeleted, deletedCount);
    }

    public static void RecordAlertReplayRequest(AlertReplayMetricResult result)
    {
        AlertReplayRequests.Add(1, AlertReplayResultTag(result));
    }

    public static void UpdateAlertOutboxStatistics(
        long pendingCount,
        TimeSpan? oldestPendingAge,
        long deadLetterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pendingCount);
        ArgumentOutOfRangeException.ThrowIfNegative(deadLetterCount);

        Volatile.Write(ref _alertPendingCount, pendingCount);
        Volatile.Write(
            ref _alertOldestPendingAgeSeconds,
            Math.Max(0, oldestPendingAge?.TotalSeconds ?? 0));
        Volatile.Write(ref _alertDeadLetterCount, deadLetterCount);
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

    private static KeyValuePair<string, object?> AlertDeliveryResultTag(AlertDeliveryMetricResult result)
    {
        return result switch
        {
            AlertDeliveryMetricResult.Delivered => new KeyValuePair<string, object?>("result", "delivered"),
            AlertDeliveryMetricResult.RetryScheduled => new KeyValuePair<string, object?>("result", "retry_scheduled"),
            AlertDeliveryMetricResult.DeadLettered => new KeyValuePair<string, object?>("result", "dead_lettered"),
            AlertDeliveryMetricResult.ClaimLost => new KeyValuePair<string, object?>("result", "claim_lost"),
            AlertDeliveryMetricResult.Interrupted => new KeyValuePair<string, object?>("result", "interrupted"),
            AlertDeliveryMetricResult.Faulted => new KeyValuePair<string, object?>("result", "faulted"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result,
                "Alert delivery metric result is not supported.")
        };
    }

    private static KeyValuePair<string, object?> AlertReplayResultTag(AlertReplayMetricResult result)
    {
        return result switch
        {
            AlertReplayMetricResult.Accepted => new KeyValuePair<string, object?>("result", "accepted"),
            AlertReplayMetricResult.Idempotent => new KeyValuePair<string, object?>("result", "idempotent"),
            AlertReplayMetricResult.Conflict => new KeyValuePair<string, object?>("result", "conflict"),
            AlertReplayMetricResult.Invalid => new KeyValuePair<string, object?>("result", "invalid"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result,
                "Alert replay metric result is not supported.")
        };
    }
}
