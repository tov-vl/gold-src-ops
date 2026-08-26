namespace GoldSrcOps.Application.Alerts;

public interface IAlertDeliveryReadRepository
{
    Task<IReadOnlyList<DeadLetterListItemDto>> ListDeadLettersAsync(
        DeadLetterPagePosition? position,
        int maxCount,
        CancellationToken cancellationToken);

    Task<DeadLetterDetailsDto?> GetDeadLetterAsync(
        Guid eventId,
        CancellationToken cancellationToken);
}
