namespace GoldSrcOps.Application.GameEvents;

public sealed record GameEventRetentionResult(
    DateTimeOffset CutoffUtc,
    int DeletedEvents,
    bool BatchLimitReached);
