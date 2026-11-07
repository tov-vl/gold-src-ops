namespace GoldSrcOps.Application.Monitoring;

internal static class GameHostWarningsProjection
{
    private static readonly Dictionary<string, (string Component, string Description)> Definitions = new(StringComparer.Ordinal)
    {
        ["GameEventAgentMonitorUnavailable"] = ("game-event-agent", "No fresh agent-monitor observations. Agent state is unknown."),
        ["GameEventAgentObservationInvalid"] = ("game-event-agent", "The monitor is running but its agent snapshot is unavailable or invalid."),
        ["GameEventAgentWorkerProblem"] = ("game-event-agent", "The agent service or an enabled worker loop needs attention."),
        ["GameEventAgentQueueDelayed"] = ("game-event-agent", "Events have waited too long while delivery is enabled. Do not replay uncertain delivery."),
        ["GameEventAgentSpoolProblem"] = ("game-event-agent", "Dead-letter records or rejected/unavailable spool data need review. Do not delete evidence."),
        ["PlayerBackupMonitorUnavailable"] = ("player-backup", "No fresh backup-monitor observations. Backup state is unknown."),
        ["PlayerBackupObservationInvalid"] = ("player-backup", "The backup journal observation failed validation. Do not remove pending records."),
        ["PlayerBackupInterventionRequired"] = ("player-backup", "A backup attempt failed or needs reconciliation. Do not retry an uncertain operation."),
        ["PlayerBackupScheduleProblem"] = ("player-backup", "The weekly backup timer or a missed slot needs review. No catch-up capture is implied."),
        ["PlayerBackupCopyUnavailable"] = ("player-backup", "The verified copy is missing or older than the existing eight-day policy."),
        ["GameHostStorageMonitorUnavailable"] = ("game-host-storage", "No fresh storage-monitor observations. Available space is unknown."),
        ["GameHostStorageObservationInvalid"] = ("game-host-storage", "Storage observations are invalid or incomplete for the fixed roles."),
        ["GameHostStorageBytesLow"] = ("game-host-storage", "Available disk space is below an existing warning threshold. No automatic cleanup runs."),
        ["GameHostStorageInodesLow"] = ("game-host-storage", "Available inodes are below an existing warning threshold on a supported filesystem."),
        ["GameHostResourceMonitorUnavailable"] = ("game-host-resources", "No fresh RAM/CPU monitor observations. Resource state is unknown."),
        ["GameHostResourceObservationInvalid"] = ("game-host-resources", "A RAM or CPU observation is invalid. Valid RAM remains independent of CPU."),
        ["GameHostMemoryLow"] = ("game-host-resources", "Available RAM is below an existing warning threshold. No automatic restart runs.")
    };

    public static GameHostWarningsDto Create(IReadOnlyList<GameHostWarningSample>? samples, DateTimeOffset now)
    {
        if (samples is null || samples.Count != Definitions.Count)
        {
            return new GameHostWarningsDto("Unknown", ObservedAtUtc: null, []);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<GameHostWarningDto>();
        var observedAt = now;
        foreach (var sample in samples)
        {
            if (!Definitions.TryGetValue(sample.Name, out var definition) || !seen.Add(sample.Name) ||
                !string.Equals(sample.Component, definition.Component, StringComparison.Ordinal) ||
                !string.Equals(sample.Group, "goldsrcops-" + definition.Component, StringComparison.Ordinal) ||
                !string.Equals(sample.Severity, "warning", StringComparison.Ordinal) ||
                !string.Equals(sample.Health, "ok", StringComparison.Ordinal) ||
                sample.State is not ("inactive" or "pending" or "firing") || sample.LastEvaluationUtc > now)
            {
                return new GameHostWarningsDto("Unknown", ObservedAtUtc: null, []);
            }

            if (sample.LastEvaluationUtc < observedAt)
            {
                observedAt = sample.LastEvaluationUtc;
            }

            if (!string.Equals(sample.State, "inactive", StringComparison.Ordinal))
            {
                items.Add(new GameHostWarningDto(sample.Name, definition.Component,
                    string.Equals(sample.State, "firing", StringComparison.Ordinal) ? "Firing" : "Pending", definition.Description));
            }
        }

        return observedAt < now.AddSeconds(-60)
            ? new GameHostWarningsDto("Stale", observedAt, [])
            : new GameHostWarningsDto("Available", observedAt,
                [.. items.OrderBy(static item => string.Equals(item.State, "Firing", StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(static item => item.Component, StringComparer.Ordinal).ThenBy(static item => item.Name, StringComparer.Ordinal)]);
    }
}
