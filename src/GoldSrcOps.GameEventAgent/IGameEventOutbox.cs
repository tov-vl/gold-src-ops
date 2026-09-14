namespace GoldSrcOps.GameEventAgent;

internal interface IGameEventOutbox
{
    void Initialize();

    QueuedGameEvent Enqueue(GameEventSourceInput input, DateTimeOffset enqueuedAtUtc);

    QueuedGameEvent? ClaimNext(DateTimeOffset nowUtc, TimeSpan leaseDuration);

    void Acknowledge(Guid eventId);

    void Reschedule(Guid eventId, DateTimeOffset nextAttemptAtUtc, GameEventDeliveryFailure failure);

    void MoveToDeadLetter(Guid eventId, GameEventDeliveryFailure failure);

    GameEventQueueState GetState();

    GameEventQueueStatistics GetStatistics();
}

internal sealed class GameEventQueueFullException(int capacity)
    : InvalidOperationException($"The game-event queue reached its configured capacity of {capacity} entries.");
