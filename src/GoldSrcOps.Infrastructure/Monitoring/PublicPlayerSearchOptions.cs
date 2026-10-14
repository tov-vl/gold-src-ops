using GoldSrcOps.Application.Monitoring;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal static class PublicPlayerSearchOptions
{
    public static PublicPlayerSearchSettings FromConfiguration(IConfiguration configuration)
    {
        var enabled = configuration["PublicPlayerSearch:Enabled"];
        if (enabled is null || string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, Guid.Empty);
        }

        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(configuration["PublicServerJoin:ServerId"], out var serverId) || serverId == Guid.Empty)
        {
            throw new InvalidOperationException("PublicPlayerSearch:Enabled requires a boolean and the configured PublicServerJoin:ServerId.");
        }

        return new(true, serverId);
    }
}
