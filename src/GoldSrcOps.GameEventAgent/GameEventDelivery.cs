namespace GoldSrcOps.GameEventAgent;

internal interface IGameEventDeliveryClient
{
    Task<GameEventDeliveryResult> SendAsync(
        QueuedGameEvent gameEvent,
        CancellationToken cancellationToken);
}

internal interface IGameEventAccessTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    void Invalidate();
}

internal enum GameEventDeliveryOutcome
{
    Accepted,
    Idempotent,
    RetryableFailure,
    PermanentFailure
}

internal enum GameEventDeliveryFailure
{
    None,
    Unauthorized,
    Forbidden,
    ServerNotFound,
    Throttled,
    RemoteServer,
    Network,
    InvalidReceipt,
    Conflict,
    InvalidRequest,
    Rejected,
    Expired,
    MaximumAttempts
}

internal sealed record GameEventDeliveryResult(
    GameEventDeliveryOutcome Outcome,
    GameEventDeliveryFailure Failure = GameEventDeliveryFailure.None)
{
    public static GameEventDeliveryResult Accepted { get; } =
        new(GameEventDeliveryOutcome.Accepted);

    public static GameEventDeliveryResult Idempotent { get; } =
        new(GameEventDeliveryOutcome.Idempotent);

    public static GameEventDeliveryResult Retryable(GameEventDeliveryFailure failure) =>
        new(GameEventDeliveryOutcome.RetryableFailure, failure);

    public static GameEventDeliveryResult Permanent(GameEventDeliveryFailure failure) =>
        new(GameEventDeliveryOutcome.PermanentFailure, failure);
}

internal sealed class GameEventTokenException : Exception
{
    public GameEventTokenException(string message)
        : base(message)
    {
    }

    public GameEventTokenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
