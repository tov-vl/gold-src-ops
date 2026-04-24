using GoldSrcOps.Application.Common;

namespace GoldSrcOps.Infrastructure;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
