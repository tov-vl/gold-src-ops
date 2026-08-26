namespace GoldSrcOps.Application.Alerts;

public sealed class ExponentialJitterAlertRetryDelayProvider : IAlertRetryDelayProvider
{
    private readonly AlertDispatcherSettings _settings;

    public ExponentialJitterAlertRetryDelayProvider(AlertDispatcherSettings settings)
    {
        _settings = settings;
    }

    public TimeSpan GetDelay(int attemptCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptCount);

        var maximumTicks = _settings.MaximumRetryDelay.Ticks;
        var ceilingTicks = _settings.BaseRetryDelay.Ticks;

        for (var exponent = 1; exponent < attemptCount && ceilingTicks < maximumTicks; exponent++)
        {
            ceilingTicks = ceilingTicks > maximumTicks / 2
                ? maximumTicks
                : Math.Min(maximumTicks, ceilingTicks * 2);
        }

        var floorTicks = Math.Max(_settings.BaseRetryDelay.Ticks, ceilingTicks / 2);
        var jitteredTicks = floorTicks +
            (long)((ceilingTicks - floorTicks) * Random.Shared.NextDouble());

        return TimeSpan.FromTicks(jitteredTicks);
    }
}
