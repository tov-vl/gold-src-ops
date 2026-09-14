namespace GoldSrcOps.GameEventAgent;

internal sealed class GameEventDispatcher
{
    private readonly IGameEventOutbox _outbox;
    private readonly IGameEventDeliveryClient _deliveryClient;
    private readonly IRetryDelayPolicy _retryDelayPolicy;
    private readonly GameEventDeliveryOptions _options;
    private readonly TimeProvider _timeProvider;

    public GameEventDispatcher(
        IGameEventOutbox outbox,
        IGameEventDeliveryClient deliveryClient,
        IRetryDelayPolicy retryDelayPolicy,
        GameEventDeliveryOptions options,
        TimeProvider timeProvider)
    {
        _outbox = outbox;
        _deliveryClient = deliveryClient;
        _retryDelayPolicy = retryDelayPolicy;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<GameEventDispatchSummary> DispatchAvailableAsync(
        CancellationToken cancellationToken)
    {
        var acknowledged = 0;
        var retried = 0;
        var deadLettered = 0;

        for (var index = 0; index < _options.BatchSize; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nowUtc = _timeProvider.GetUtcNow();
            var gameEvent = _outbox.ClaimNext(nowUtc, _options.LeaseDuration);
            if (gameEvent is null)
            {
                break;
            }

            if (nowUtc - gameEvent.CreatedAtUtc >= _options.MaximumEventAge)
            {
                _outbox.MoveToDeadLetter(gameEvent.EventId, GameEventDeliveryFailure.Expired);
                deadLettered++;
                continue;
            }

            var result = await _deliveryClient
                .SendAsync(gameEvent, cancellationToken)
                .ConfigureAwait(false);
            switch (result.Outcome)
            {
                case GameEventDeliveryOutcome.Accepted:
                case GameEventDeliveryOutcome.Idempotent:
                    _outbox.Acknowledge(gameEvent.EventId);
                    acknowledged++;
                    break;
                case GameEventDeliveryOutcome.PermanentFailure:
                    _outbox.MoveToDeadLetter(gameEvent.EventId, result.Failure);
                    deadLettered++;
                    break;
                case GameEventDeliveryOutcome.RetryableFailure
                    when gameEvent.AttemptCount >= _options.MaximumAttempts:
                    _outbox.MoveToDeadLetter(
                        gameEvent.EventId,
                        GameEventDeliveryFailure.MaximumAttempts);
                    deadLettered++;
                    break;
                case GameEventDeliveryOutcome.RetryableFailure:
                    _outbox.Reschedule(
                        gameEvent.EventId,
                        _timeProvider.GetUtcNow() + _retryDelayPolicy.GetDelay(gameEvent.AttemptCount),
                        result.Failure);
                    retried++;
                    break;
                default:
                    throw new InvalidOperationException("The game-event delivery result is unsupported.");
            }
        }

        return new GameEventDispatchSummary(acknowledged, retried, deadLettered);
    }
}

internal sealed record GameEventDispatchSummary(
    int Acknowledged,
    int Retried,
    int DeadLettered)
{
    public int Processed => Acknowledged + Retried + DeadLettered;
}
