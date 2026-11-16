namespace GoldSrcOps.TelemetryAcceptance;

internal sealed record WorkerCycles(DateTimeOffset StartedAtUtc, string? ImportOutcome, string? DeliveryOutcome,
    DateTimeOffset? ImportCompletedAtUtc, DateTimeOffset? DeliveryCompletedAtUtc)
{
    public bool BothCompleted => ImportCompletedAtUtc >= StartedAtUtc && DeliveryCompletedAtUtc >= StartedAtUtc;
}
