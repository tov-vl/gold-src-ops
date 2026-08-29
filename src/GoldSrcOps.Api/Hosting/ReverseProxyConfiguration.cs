using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace GoldSrcOps.Api.Hosting;

internal static class ReverseProxyConfiguration
{
    private const string KnownProxyConfigurationKey = "ReverseProxy:KnownProxy";

    public static bool Configure(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredAddress = configuration[KnownProxyConfigurationKey];
        if (string.IsNullOrWhiteSpace(configuredAddress))
        {
            return false;
        }

        if (!IPAddress.TryParse(configuredAddress.Trim(), out var knownProxy))
        {
            throw new InvalidOperationException(
                $"Configuration value '{KnownProxyConfigurationKey}' must be a valid IP address.");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownProxies.Add(knownProxy);
        });

        return true;
    }
}
