using AwesomeAssertions;
using GoldSrcOps.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.UnitTests.Api;

public sealed class GameHostHealthConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("http://user:password@example.test/")]
    [InlineData("http://example.test/private/")]
    [InlineData("http://example.test/?token=secret")]
    [InlineData("file:///private")]
    public void Enabled_source_requires_a_trusted_origin_at_startup(string? endpoint)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GameHostHealth:Enabled"] = "true",
                ["GameHostHealth:PrometheusBaseUrl"] = endpoint
            }).Build();
        var services = new ServiceCollection();
        var action = () => services.AddGameHostHealthSource(configuration);
        action.Should().Throw<InvalidOperationException>();
    }
}
