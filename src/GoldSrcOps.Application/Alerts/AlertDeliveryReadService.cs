namespace GoldSrcOps.Application.Alerts;

public sealed class AlertDeliveryReadService
{
    public const int DefaultDeadLetterLimit = 50;
    public const int MaxDeadLetterLimit = 200;

    private readonly IAlertDeliveryReadRepository _repository;

    public AlertDeliveryReadService(IAlertDeliveryReadRepository repository)
    {
        _repository = repository;
    }

    public async Task<DeadLetterPageDto> ListDeadLettersAsync(
        DeadLetterPagePosition? position,
        int? limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = limit ?? DefaultDeadLetterLimit;
        if (effectiveLimit is < 1 or > MaxDeadLetterLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Limit must be between 1 and {MaxDeadLetterLimit}.");
        }

        var rows = await _repository.ListDeadLettersAsync(
            position,
            effectiveLimit + 1,
            cancellationToken);
        var hasMore = rows.Count > effectiveLimit;
        var items = hasMore ? rows.Take(effectiveLimit).ToArray() : rows;
        var nextPosition = hasMore
            ? new DeadLetterPagePosition(
                items[^1].DeadLetteredAtUtc,
                items[^1].OccurredAtUtc,
                items[^1].EventId)
            : null;

        return new DeadLetterPageDto(effectiveLimit, items, nextPosition);
    }

    public Task<DeadLetterDetailsDto?> GetDeadLetterAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        return _repository.GetDeadLetterAsync(eventId, cancellationToken);
    }
}
