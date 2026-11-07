namespace GoldSrcOps.Web.Services;

internal sealed record GameHostWarningGuidance(string FirstCheck, string Preserve, string RunbookUrl)
{
    private const string Runbook = "https://github.com/tov-vl/gold-src-ops/blob/main/docs/game-host-warning-triage.md";

    private static readonly Dictionary<string, GameHostWarningGuidance> Warnings = new(StringComparer.Ordinal)
    {
        ["GameEventAgentMonitorUnavailable"] = new("Compare the agent-monitor timestamp with its timer/service and private collection path.", "Keep the last observation and collector failure; an unread queue is not an empty queue.", Runbook + "#agent-monitor-unavailable"),
        ["GameEventAgentObservationInvalid"] = new("Check the observer failure and passive agent status for a missing, old or invalid snapshot.", "Keep the validation failure and timestamps. Do not initialize or repair the queue through status.", Runbook + "#agent-observation-invalid"),
        ["GameEventAgentWorkerProblem"] = new("Compare enabled worker loops, process state and their last completed/successful cycles.", "Keep the runtime journal and enabled flags before considering a restart.", Runbook + "#agent-worker-problem"),
        ["GameEventAgentQueueDelayed"] = new("Compare pending/in-flight counts, oldest enqueue age, backoff and delivery outcomes.", "Keep request outcomes and queue evidence. Do not replay uncertain delivery.", Runbook + "#agent-queue-delayed"),
        ["GameEventAgentSpoolProblem"] = new("Check dead-letter counts, spool availability and rejected-file counts in the existing diagnostics.", "Preserve rejected files and queue records; do not delete evidence or replay to clear the warning.", Runbook + "#agent-spool-problem"),
        ["PlayerBackupMonitorUnavailable"] = new("Compare the backup-monitor timestamp with its timer/service and fixed read path.", "Keep collector failures and the last observation; missing monitoring does not prove a missing copy.", Runbook + "#backup-monitor-unavailable"),
        ["PlayerBackupObservationInvalid"] = new("Check the bounded journal-validation failure and the consistency of the existing attempt records.", "Preserve original journal bytes and pending records. Do not edit them to make validation pass.", Runbook + "#backup-observation-invalid"),
        ["PlayerBackupInterventionRequired"] = new("Compare the last attempt, pending state and backup-service result before choosing recovery.", "Preserve original attempt records and copy time. Do not retry an uncertain backup or reset its failure blindly.", Runbook + "#backup-intervention-required"),
        ["PlayerBackupScheduleProblem"] = new("Compare the timer's enabled state, next activation and missed-slot observation with the documented schedule.", "Keep schedule and slot evidence. A missed slot does not authorize a catch-up capture.", Runbook + "#backup-schedule-problem"),
        ["PlayerBackupCopyUnavailable"] = new("Check the verified copy's original capture time, source and age against the existing eight-day policy.", "Keep the last verified-copy record. A newer manual copy does not close an older failed weekly attempt.", Runbook + "#backup-copy-unavailable"),
        ["GameHostStorageMonitorUnavailable"] = new("Compare the storage-monitor timestamp with its timer/service and fixed collection path.", "Keep the last observation and collector failure; unavailable capacity is not zero free space.", Runbook + "#storage-monitor-unavailable"),
        ["GameHostStorageObservationInvalid"] = new("Check source freshness and availability for each of the four fixed storage roles.", "Keep role availability and validation evidence. Missing or unsupported inode data is not zero free inodes.", Runbook + "#storage-observation-invalid"),
        ["GameHostStorageBytesLow"] = new("Compare available bytes and ratios for each valid storage role with the existing thresholds.", "Record the affected role and observation time privately. Do not delete queues, player data or backups to clear the warning.", Runbook + "#storage-bytes-low"),
        ["GameHostStorageInodesLow"] = new("Compare available inode counts and ratios for roles with supported inode accounting.", "Keep supported-accounting and role evidence. Cleanup requires its own reviewed scope.", Runbook + "#storage-inodes-low"),
        ["GameHostResourceMonitorUnavailable"] = new("Compare the resource-monitor timestamp with its timer/service and fixed collection path.", "Keep collection failures and the last observation; an unread host is not a zero-load host.", Runbook + "#resource-monitor-unavailable"),
        ["GameHostResourceObservationInvalid"] = new("Check source freshness and the independent RAM/CPU availability fields.", "Keep both groups' validity evidence. A CPU sampling failure does not invalidate usable RAM observations.", Runbook + "#resource-observation-invalid"),
        ["GameHostMemoryLow"] = new("Compare available RAM bytes and ratio with the existing thresholds and observation time.", "Keep the RAM/swap observation. This is not an OOM diagnosis and does not authorize restarting the game or agent.", Runbook + "#memory-low")
    };

    public static GameHostWarningGuidance Unknown { get; } = new(
        "Refresh once, then compare the API snapshot and existing private monitoring source if data remains unavailable.",
        "Keep observation times and the bounded failure. Unknown does not mean healthy or offline; do not repair data or restart services just to obtain a green card.",
        Runbook + "#unknown-snapshot");

    public static GameHostWarningGuidance Stale { get; } = new(
        "Compare the page evaluation time with the oldest rule evaluation, then check the existing rule evaluator and collection freshness.",
        "Keep both timestamps. Old observations do not establish current warning state; do not treat an old empty list as healthy.",
        Runbook + "#stale-snapshot");

    public const string RunbookHome = Runbook;

    public static GameHostWarningGuidance? ForWarning(string name) => Warnings.GetValueOrDefault(name);
}
