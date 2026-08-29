using System.Net;
using GoldSrcOps.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.UnitTests.Api;

public sealed class ReverseProxyConfigurationTests
{
    [Fact]
    public void Configure_returns_false_when_known_proxy_is_not_configured()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var enabled = ReverseProxyConfiguration.Configure(services, configuration);

        Assert.False(enabled);
    }

    [Fact]
    public void Configure_registers_bounded_forwarded_headers_for_known_proxy()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration("172.31.246.2");

        var enabled = ReverseProxyConfiguration.Configure(services, configuration);

        Assert.True(enabled);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>()
            .Value;

        Assert.Equal(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Equal([IPAddress.Parse("172.31.246.2")], options.KnownProxies);
    }

    [Fact]
    public void Configure_rejects_invalid_known_proxy()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration("caddy");

        var exception = Assert.Throws<InvalidOperationException>(
            () => ReverseProxyConfiguration.Configure(services, configuration));

        Assert.True(exception.Message.Contains("ReverseProxy:KnownProxy", StringComparison.Ordinal));
    }

    private static IConfiguration CreateConfiguration(string knownProxy) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ReverseProxy:KnownProxy"] = knownProxy,
            })
            .Build();
}
