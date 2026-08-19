namespace GoldSrcOps.Application.Monitoring;

public sealed record SnapshotRetentionResult(
    DateTimeOffset CutoffUtc,
    int DeletedSnapshots,
    bool BatchLimitReached);
