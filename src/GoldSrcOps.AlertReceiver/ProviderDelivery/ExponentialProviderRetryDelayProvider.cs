using GoldSrcOps.AlertReceiver.Configuration;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.AlertReceiver.ProviderDelivery;

internal sealed class ExponentialProviderRetryDelayProvider(
    IOptions<ProviderDeliveryOptions> options) : IProviderRetryDelayProvider
{
    private readonly ProviderDeliveryOptions _options = options.Value;

    public TimeSpan GetDelay(int attemptCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptCount);

        var maximumTicks = _options.MaximumRetryDelay.Ticks;
        var ceilingTicks = _options.BaseRetryDelay.Ticks;
        for (var exponent = 1; exponent < attemptCount && ceilingTicks < maximumTicks; exponent++)
        {
            ceilingTicks = ceilingTicks > maximumTicks / 2
                ? maximumTicks
                : Math.Min(maximumTicks, ceilingTicks * 2);
        }

        var floorTicks = Math.Max(_options.BaseRetryDelay.Ticks, ceilingTicks / 2);
        return TimeSpan.FromTicks(
            floorTicks + (long)((ceilingTicks - floorTicks) * Random.Shared.NextDouble()));
    }
}
