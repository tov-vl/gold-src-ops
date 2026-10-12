using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.WebTests.Pages;

public sealed class PublicStatusRefreshIntegrationTests
{
    [Theory]
    [InlineData("ru", 0, "online", "operational", "online")]
    [InlineData("en", 0, "offline", "operational", "offline")]
    [InlineData("ru", 200, "online", "unknown", "unknown")]
    [InlineData("en", -1, "online", "unknown", "unknown")]
    public async Task Status_snapshot_age_controls_initial_claims_without_javascript(
        string language, int age, string serverState, string expectedFleet, string expectedServer)
    {
        DateTimeOffset? observed = age < 0 ? null : DateTimeOffset.UtcNow.AddSeconds(-age);
        await using var factory = new PublicDashboardWebApplicationFactory(
            status: new("operational", 1, 1, 0, 0, observed),
            serverJoinResponse: new("Public server", "play.example.test", 27015, serverState,
                "de_current", 3, 20, observed));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/?range=7d&lang=" + language);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("data-status-fleet").And.Contain("data-status-server")
            .And.Contain("data-status-history").And.Contain("data-window=\"7d\"")
            .And.Contain($"data-state=\"{expectedFleet}\"").And.Contain($"data-state=\"{expectedServer}\"")
            .And.Contain("99.5%").And.Contain("connect play.example.test:27015");
        if (!string.Equals(expectedServer, "online", StringComparison.Ordinal)) body.Should().NotContain("de_current").And.NotContain("3 / 20");
        if (string.Equals(expectedFleet, "unknown", StringComparison.Ordinal)) body.Should().Contain("data-stale=\"true\"");
        body.Should().NotContain(PublicDashboardWebApplicationFactory.PrivateDataSentinel);
        response.Headers.Should().NotContain(header => header.Key == "Set-Cookie");
        factory.Handler.RequestedPaths.Should().BeEquivalentTo("/api/public/status", "/api/public/server", "/api/public/a2s-history");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "false")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "true")]
    public async Task Status_distinguishes_removed_connection_configuration_from_failed_projection(HttpStatusCode code, string failed)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(serverJoinFailureStatus: code);
        using var client = factory.CreateClient();
        var body = await client.GetStringAsync("/?lang=en");
        body.Should().Contain($"data-failed=\"{failed}\"").And.NotContain("data-copy-connect")
            .And.NotContain(PublicDashboardWebApplicationFactory.PrivateDataSentinel);
    }
}
