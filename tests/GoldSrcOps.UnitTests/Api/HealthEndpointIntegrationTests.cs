using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GoldSrcOps.UnitTests.Api;

public sealed class HealthEndpointIntegrationTests
{
    [Fact]
    public async Task GetHealthReady_returns_healthy_when_database_is_available()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Healthy");
    }

    [Fact]
    public async Task GetHealthLive_does_not_run_readiness_checks()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        await using var liveFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddHealthChecks()
                    .AddCheck(
                        "forced-readiness-failure",
                        static () => HealthCheckResult.Unhealthy(),
                        tags: ["ready"]);
            });
        });
        using var client = liveFactory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Healthy");
    }
}
