namespace GoldSrcOps.GameEventAgent;

internal interface IRetryDelayPolicy
{
    TimeSpan GetDelay(int attemptCount);
}

internal sealed class ExponentialRetryDelayPolicy(GameEventDeliveryOptions options)
    : IRetryDelayPolicy
{
    public TimeSpan GetDelay(int attemptCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptCount);

        var exponent = Math.Min(attemptCount - 1, 30);
        var delayTicks = Math.Min(
            options.RetryBaseDelay.Ticks * Math.Pow(2, exponent),
            options.RetryMaximumDelay.Ticks);
        return TimeSpan.FromTicks(checked((long)delayTicks));
    }
}
