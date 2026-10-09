using GoldSrcOps.Application.Monitoring;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal static class PublicLeaderboardOptions
{
    public static PublicLeaderboardSettings FromConfiguration(IConfiguration configuration)
    {
        var value = configuration["PublicLeaderboard:Enabled"];
        if (value is null || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, Guid.Empty);
        }

        if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(configuration["PublicServerJoin:ServerId"], out var serverId) || serverId == Guid.Empty)
        {
            throw new InvalidOperationException("PublicLeaderboard:Enabled requires a boolean and the configured PublicServerJoin:ServerId.");
        }

        return new(true, serverId);
    }
}
