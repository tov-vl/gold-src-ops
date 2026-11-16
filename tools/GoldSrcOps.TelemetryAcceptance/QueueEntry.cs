namespace GoldSrcOps.TelemetryAcceptance;

internal sealed record QueueEntry(QueueIdentity Identity, int Status, int Attempts, string? Failure);
