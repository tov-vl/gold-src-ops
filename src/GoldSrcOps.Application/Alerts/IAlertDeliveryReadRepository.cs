namespace GoldSrcOps.Application.Alerts;

public interface IAlertDeliveryReadRepository
{
    Task<AlertDeliveryQueueStatistics> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingDeliveryListItemDto>> ListPendingDeliveriesAsync(
        PendingDeliveryPagePosition? position,
        int maxCount,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DeadLetterListItemDto>> ListDeadLettersAsync(
        DeadLetterPagePosition? position,
        int maxCount,
        CancellationToken cancellationToken);

    Task<DeadLetterDetailsDto?> GetDeadLetterAsync(
        Guid eventId,
        CancellationToken cancellationToken);
}
