namespace GoldSrcOps.Application.Common;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
