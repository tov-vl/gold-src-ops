namespace GoldSrcOps.Application.Alerts;

public interface IAlertDeliveryReplayRepository
{
    Task<DeadLetterReplayResult> ReplayAsync(
        Guid requestId,
        Guid eventId,
        string requestedBy,
        DateTimeOffset requestedAtUtc,
        string reason,
        CancellationToken cancellationToken);

    Task<DeadLetterReplayRecordDto?> GetReplayAsync(
        Guid requestId,
        CancellationToken cancellationToken);
}
