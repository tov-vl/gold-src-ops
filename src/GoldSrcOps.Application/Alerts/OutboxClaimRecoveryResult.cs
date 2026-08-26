namespace GoldSrcOps.Application.Alerts;

public sealed record OutboxClaimRecoveryResult(
    int RetryScheduled,
    int DeadLettered)
{
    public int TotalRecovered => RetryScheduled + DeadLettered;
}
