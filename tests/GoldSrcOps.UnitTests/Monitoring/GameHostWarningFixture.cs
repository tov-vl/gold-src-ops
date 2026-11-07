using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.UnitTests.Monitoring;

internal static class GameHostWarningFixture
{
    public static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public static List<GameHostWarningSample> Fresh() =>
    [
        Rule("game-event-agent", "GameEventAgentMonitorUnavailable"),
        Rule("game-event-agent", "GameEventAgentObservationInvalid"),
        Rule("game-event-agent", "GameEventAgentWorkerProblem"),
        Rule("game-event-agent", "GameEventAgentQueueDelayed"),
        Rule("game-event-agent", "GameEventAgentSpoolProblem"),
        Rule("player-backup", "PlayerBackupMonitorUnavailable"),
        Rule("player-backup", "PlayerBackupObservationInvalid"),
        Rule("player-backup", "PlayerBackupInterventionRequired"),
        Rule("player-backup", "PlayerBackupScheduleProblem"),
        Rule("player-backup", "PlayerBackupCopyUnavailable"),
        Rule("game-host-storage", "GameHostStorageMonitorUnavailable"),
        Rule("game-host-storage", "GameHostStorageObservationInvalid"),
        Rule("game-host-storage", "GameHostStorageBytesLow"),
        Rule("game-host-storage", "GameHostStorageInodesLow"),
        Rule("game-host-resources", "GameHostResourceMonitorUnavailable"),
        Rule("game-host-resources", "GameHostResourceObservationInvalid"),
        Rule("game-host-resources", "GameHostMemoryLow")
    ];

    private static GameHostWarningSample Rule(string component, string name) =>
        new("goldsrcops-" + component, name, component, "warning", "inactive", "ok", Now);
}
