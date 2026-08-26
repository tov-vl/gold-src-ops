namespace GoldSrcOps.Application.Alerts;

public interface IAlertDeliveryChannel
{
    Task<AlertDeliveryAttemptResult> DeliverAsync(
        ClaimedOutboxMessage message,
        CancellationToken cancellationToken);
}
