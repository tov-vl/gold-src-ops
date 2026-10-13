using AwesomeAssertions;
using GoldSrcOps.Web.Hosting;
using GoldSrcOps.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace GoldSrcOps.WebTests.Hosting;

public sealed class WebTelemetryConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Export_is_opt_in(string? enabled)
    {
        var services = new ServiceCollection();
        WebTelemetryConfiguration.Configure(services, Configuration(enabled, null));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<PublicApiMetrics>().Should().NotBeNull();
        provider.GetService<MeterProvider>().Should().BeNull();
    }

    [Theory]
    [InlineData("invalid", "http://collector:4317")]
    [InlineData("true", null)]
    [InlineData("true", "file:///tmp/metrics")]
    [InlineData("true", "http://user:secret@collector:4317")]
    [InlineData("true", "http://collector:4317?secret=value")]
    [InlineData("true", "http://collector:4317#fragment")]
    [InlineData("true", "http://collector:4317/path")]
    public void Invalid_configuration_fails_without_echoing_values(string enabled, string? endpoint)
    {
        var action = () => WebTelemetryConfiguration.Configure(new ServiceCollection(), Configuration(enabled, endpoint));

        action.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("secret");
    }

    private static IConfiguration Configuration(string? enabled, string? endpoint) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Telemetry:Otlp:Enabled"] = enabled,
            ["Telemetry:Otlp:Endpoint"] = endpoint
        }).Build();
}
