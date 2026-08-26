namespace GoldSrcOps.Application.Alerts;

public sealed record OutboxCleanupResult(
    DateTimeOffset CutoffUtc,
    int DeletedMessages,
    bool BatchLimitReached);
