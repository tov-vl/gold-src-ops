namespace GoldSrcOps.GameEventAgent;

internal interface IGameEventOutbox
{
    void Initialize();

    QueuedGameEvent Enqueue(GameEventSourceInput input, DateTimeOffset enqueuedAtUtc);

    GameEventSpoolEnqueueResult EnqueueFromSpool(
        Guid recordId,
        byte[] contentSha256,
        GameEventSourceInput input,
        DateTimeOffset enqueuedAtUtc);

    void CompleteSpoolRecord(Guid recordId, byte[] contentSha256);

    QueuedGameEvent? ClaimNext(DateTimeOffset nowUtc, TimeSpan leaseDuration);

    void Acknowledge(Guid eventId);

    void Reschedule(Guid eventId, DateTimeOffset nextAttemptAtUtc, GameEventDeliveryFailure failure);

    void MoveToDeadLetter(Guid eventId, GameEventDeliveryFailure failure);

    GameEventQueueState GetState();

    GameEventQueueStatistics GetStatistics();
}

internal sealed class GameEventQueueFullException(int capacity)
    : InvalidOperationException($"The game-event queue reached its configured capacity of {capacity} entries.");

internal sealed class GameEventSpoolReceiptCapacityException(int capacity)
    : InvalidOperationException($"The game-event spool receipt store reached its configured capacity of {capacity} entries.");

internal sealed class GameEventSpoolRecordConflictException()
    : InvalidOperationException("The game-event spool record ID was reused with different content.");
